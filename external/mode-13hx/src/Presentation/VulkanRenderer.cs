using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace mode13hx.Presentation;

public unsafe class VulkanRenderer : IDisposable
{
    private readonly Vk vk;
    private readonly IWindow window;

    // Instance & surface
    private Instance instance;
    private KhrSurface khrSurfaceExt;
    private SurfaceKHR surface;

    // Device
    private PhysicalDevice physicalDevice;
    private Device device;
    private Queue graphicsQueue;
    private Queue presentQueue;
    private uint graphicsFamily;
    private uint presentFamily;

    // Swapchain
    private KhrSwapchain khrSwapchainExt;
    private SwapchainKHR swapchain;
    private Image[] swapchainImages;
    private Format swapchainImageFormat;
    private Extent2D swapchainExtent;
    private ImageView[] swapchainImageViews;

    // Pipeline
    private RenderPass renderPass;
    private PipelineLayout pipelineLayout;
    private Pipeline graphicsPipeline;

    // Framebuffers
    private Framebuffer[] framebuffers;

    // Commands
    private CommandPool commandPool;
    private CommandBuffer[] commandBuffers;

    // Sync
    private Semaphore[] imageAvailableSemaphores;
    private Semaphore[] renderFinishedSemaphores;
    private Fence[] inFlightFences;
    private int currentFrame;

    // Vertex & index buffers
    private Buffer vertexBuffer;
    private DeviceMemory vertexBufferMemory;
    private Buffer indexBuffer;
    private DeviceMemory indexBufferMemory;

    // Texture (CPU-rasterized frame)
    private Image textureImage;
    private DeviceMemory textureImageMemory;
    private ImageView textureImageView;
    private Silk.NET.Vulkan.Sampler textureSampler;
    private Buffer stagingBuffer;
    private DeviceMemory stagingBufferMemory;
    private void* stagingBufferMapped;
    private readonly uint textureWidth;  // = frame Height (column-major layout)
    private readonly uint textureHeight; // = frame Width
    private readonly ulong stagingBufferSize;

    // Descriptors (graphics)
    private DescriptorSetLayout descriptorSetLayout;
    private DescriptorPool descriptorPool;
    private DescriptorSet descriptorSet;

    // Compute pipeline (frame decompression, optional)
    private bool hasComputePipeline;
    private Pipeline computePipeline;
    private PipelineLayout computePipelineLayout;
    private DescriptorSetLayout computeDescriptorSetLayout;
    private DescriptorPool computeDescriptorPool;
    // Per-frame storage buffers + descriptor sets to avoid GPU/CPU data races
    private DescriptorSet[] computeDescriptorSets;
    private Buffer[] compressedBlocksBuffers;
    private DeviceMemory[] compressedBlocksMemories;
    private void*[] compressedBlocksMappedPtrs;
    private ulong compressedBlocksBufferSize;

    // Per-frame state for compressed frame dispatch
    private bool frameIsCompressed;
    private uint computeGroupsX, computeGroupsY;

    // Config
    private readonly bool vSyncEnabled;

    // Full-screen quad: Position (vec3) + TexCoord (vec2)
    // Texcoords match the GL version's column-major texture (Width=Height, Height=Width)
    private static readonly float[] Vertices =
    [
        // Position          TexCoord (adjusted for Vulkan Y-down + column-major texture)
         1.0f,  1.0f, 0.0f, 1.0f, 1.0f, // bottom-right
         1.0f, -1.0f, 0.0f, 0.0f, 1.0f, // top-right
        -1.0f, -1.0f, 0.0f, 0.0f, 0.0f, // top-left
        -1.0f,  1.0f, 0.0f, 1.0f, 0.0f, // bottom-left
    ];

    private static readonly uint[] Indices = [0, 1, 3, 1, 2, 3];

    public VulkanRenderer(IWindow window, int frameWidth, int frameHeight, bool vSync = false)
    {
        this.window = window;
        this.vSyncEnabled = vSync;
        // Texture dimensions match the GL convention: texture width = frame height (column-major)
        textureWidth = (uint)frameHeight;
        textureHeight = (uint)frameWidth;
        stagingBufferSize = (ulong)(frameWidth * frameHeight * sizeof(uint));
        vk = Vk.GetApi();

        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateSwapchain();
        CreateImageViews();
        CreateRenderPass();
        CreateDescriptorSetLayout();
        CreateGraphicsPipeline();
        CreateFramebuffers();
        CreateCommandPool();
        CreateVertexBuffer();
        CreateIndexBuffer();
        CreateTextureResources();
        CreateDescriptorPoolAndSet();
        CreateCommandBuffers();
        CreateSyncObjects();
    }

    // ── Instance ──────────────────────────────────────────────────────────────

    private void CreateInstance()
    {
        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("mode13hx"),
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("No Engine"),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version12,
        };

        // Enumerate available instance extensions
        uint availExtCount = 0;
        vk.EnumerateInstanceExtensionProperties((byte*)null, ref availExtCount, null);
        ExtensionProperties[] availExts = new ExtensionProperties[availExtCount];
        fixed (ExtensionProperties* p = availExts)
            vk.EnumerateInstanceExtensionProperties((byte*)null, ref availExtCount, p);

        HashSet<string> availableExtNames = new HashSet<string>();
        fixed (ExtensionProperties* p = availExts)
        {
            for (uint i = 0; i < availExtCount; i++)
                availableExtNames.Add(Marshal.PtrToStringAnsi((nint)p[i].ExtensionName));
        }

        // Extensions required by the windowing system
        byte** windowExtensions = window.VkSurface!.GetRequiredExtensions(out uint windowExtCount);
        List<string> extensions = new List<string>();
        for (uint i = 0; i < windowExtCount; i++)
            extensions.Add(Marshal.PtrToStringAnsi((nint)windowExtensions[i]));

        // macOS MoltenVK portability — only if supported
        bool hasPortabilityEnum = availableExtNames.Contains("VK_KHR_portability_enumeration");
        if (hasPortabilityEnum && !extensions.Contains("VK_KHR_portability_enumeration"))
            extensions.Add("VK_KHR_portability_enumeration");
        if (availableExtNames.Contains("VK_KHR_get_physical_device_properties2") && !extensions.Contains("VK_KHR_get_physical_device_properties2"))
            extensions.Add("VK_KHR_get_physical_device_properties2");

        byte** extPtrs = (byte**)SilkMarshal.StringArrayToPtr(extensions.ToArray());

        // Validation layers (debug builds only)
        string[] validationLayers = ["VK_LAYER_KHRONOS_validation"];
        byte** layerPtrs = null;
        uint layerCount = 0;
#if DEBUG
        if (CheckValidationLayerSupport(validationLayers))
        {
            layerPtrs = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
            layerCount = (uint)validationLayers.Length;
        }
#endif

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = extPtrs,
            EnabledLayerCount = layerCount,
            PpEnabledLayerNames = layerPtrs,
            Flags = hasPortabilityEnum ? InstanceCreateFlags.EnumeratePortabilityBitKhr : 0,
        };

        Check(vk.CreateInstance(in createInfo, null, out instance), "create Vulkan instance");

        SilkMarshal.Free((nint)appInfo.PApplicationName);
        SilkMarshal.Free((nint)appInfo.PEngineName);
        SilkMarshal.Free((nint)extPtrs);
        if (layerPtrs != null) SilkMarshal.Free((nint)layerPtrs);

        if (!vk.TryGetInstanceExtension(instance, out khrSurfaceExt))
            throw new Exception("VK_KHR_surface extension not available");
    }

    private bool CheckValidationLayerSupport(string[] layers)
    {
        uint count = 0;
        vk.EnumerateInstanceLayerProperties(ref count, null);
        LayerProperties[] available = new LayerProperties[count];
        fixed (LayerProperties* p = available)
            vk.EnumerateInstanceLayerProperties(ref count, p);

        foreach (string layer in layers)
        {
            bool found = false;
            fixed (LayerProperties* pAvail = available)
            {
                for (uint i = 0; i < count; i++)
                {
                    if (Marshal.PtrToStringAnsi((nint)pAvail[i].LayerName) == layer)
                    { found = true; break; }
                }
            }
            if (!found) return false;
        }
        return true;
    }

    // ── Surface ───────────────────────────────────────────────────────────────

    private void CreateSurface()
    {
        surface = window.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
    }

    // ── Physical device ───────────────────────────────────────────────────────

    private void PickPhysicalDevice()
    {
        uint deviceCount = 0;
        vk.EnumeratePhysicalDevices(instance, ref deviceCount, null);
        if (deviceCount == 0) throw new Exception("No Vulkan-capable GPU found");

        PhysicalDevice[] devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* p = devices)
            vk.EnumeratePhysicalDevices(instance, ref deviceCount, p);

        foreach (PhysicalDevice dev in devices)
        {
            if (IsDeviceSuitable(dev))
            {
                physicalDevice = dev;
                vk.GetPhysicalDeviceProperties(dev, out PhysicalDeviceProperties props);
                Console.WriteLine($"[Vulkan] Using GPU: {Marshal.PtrToStringAnsi((nint)props.DeviceName)}");
                return;
            }
        }
        // If no device was explicitly suitable, try the first one (MoltenVK may not report all caps)
        physicalDevice = devices[0];
        vk.GetPhysicalDeviceProperties(physicalDevice, out PhysicalDeviceProperties fallbackProps);
        Console.WriteLine($"[Vulkan] Using GPU (fallback): {Marshal.PtrToStringAnsi((nint)fallbackProps.DeviceName)}");

        throw new Exception("No suitable Vulkan GPU found");
    }

    private bool IsDeviceSuitable(PhysicalDevice dev)
    {
        if (!FindQueueFamilies(dev, out uint gf, out uint pf)) return false;

        // Check required extensions
        uint extCount = 0;
        vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, ref extCount, null);
        ExtensionProperties[] exts = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* p = exts)
            vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, ref extCount, p);

        bool hasSwapchain = false;
        fixed (ExtensionProperties* pExts = exts)
        {
            for (uint i = 0; i < extCount; i++)
            {
                string name = Marshal.PtrToStringAnsi((nint)pExts[i].ExtensionName);
                if (name == KhrSwapchain.ExtensionName) hasSwapchain = true;
            }
        }
        if (!hasSwapchain) return false;

        // Check swapchain support
        khrSurfaceExt.GetPhysicalDeviceSurfaceFormats(dev, surface, ref extCount, null);
        if (extCount == 0) return false;
        khrSurfaceExt.GetPhysicalDeviceSurfacePresentModes(dev, surface, ref extCount, null);
        return extCount > 0;
    }

    private bool FindQueueFamilies(PhysicalDevice dev, out uint graphicsFam, out uint presentFam)
    {
        graphicsFam = uint.MaxValue;
        presentFam = uint.MaxValue;

        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(dev, ref count, null);
        QueueFamilyProperties[] families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
            vk.GetPhysicalDeviceQueueFamilyProperties(dev, ref count, p);

        for (uint i = 0; i < count; i++)
        {
            if (families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                graphicsFam = i;

            khrSurfaceExt.GetPhysicalDeviceSurfaceSupport(dev, i, surface, out Bool32 supported);
            if (supported) presentFam = i;

            if (graphicsFam != uint.MaxValue && presentFam != uint.MaxValue) return true;
        }

        return false;
    }

    // ── Logical device ────────────────────────────────────────────────────────

    private void CreateLogicalDevice()
    {
        FindQueueFamilies(physicalDevice, out graphicsFamily, out presentFamily);

        HashSet<uint> uniqueFamilies = new HashSet<uint> { graphicsFamily, presentFamily };
        DeviceQueueCreateInfo[] queueCreateInfos = new DeviceQueueCreateInfo[uniqueFamilies.Count];
        float priority = 1.0f;
        int idx = 0;
        foreach (uint family in uniqueFamilies)
        {
            queueCreateInfos[idx++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = family,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        PhysicalDeviceFeatures deviceFeatures = new();

        // Required extensions
        List<string> deviceExtensions = new List<string> { KhrSwapchain.ExtensionName };

        // Check for portability subset (macOS/MoltenVK)
        uint extCount = 0;
        vk.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, ref extCount, null);
        ExtensionProperties[] exts = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* p = exts)
            vk.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, ref extCount, p);
        fixed (ExtensionProperties* pExts = exts)
        {
            for (uint i = 0; i < extCount; i++)
            {
                if (Marshal.PtrToStringAnsi((nint)pExts[i].ExtensionName) == "VK_KHR_portability_subset")
                    deviceExtensions.Add("VK_KHR_portability_subset");
            }
        }

        byte** extPtrs = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions.ToArray());

        fixed (DeviceQueueCreateInfo* queuePtr = queueCreateInfos)
        {
            DeviceCreateInfo createInfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = (uint)queueCreateInfos.Length,
                PQueueCreateInfos = queuePtr,
                PEnabledFeatures = &deviceFeatures,
                EnabledExtensionCount = (uint)deviceExtensions.Count,
                PpEnabledExtensionNames = extPtrs,
            };

            Check(vk.CreateDevice(physicalDevice, in createInfo, null, out device), "create logical device");
        }

        SilkMarshal.Free((nint)extPtrs);

        vk.GetDeviceQueue(device, graphicsFamily, 0, out graphicsQueue);
        vk.GetDeviceQueue(device, presentFamily, 0, out presentQueue);

        if (!vk.TryGetDeviceExtension(instance, device, out khrSwapchainExt))
            throw new Exception("VK_KHR_swapchain extension not available");
    }

    // ── Swapchain ─────────────────────────────────────────────────────────────

    private void CreateSwapchain()
    {
        khrSurfaceExt.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out SurfaceCapabilitiesKHR caps);

        // Choose surface format
        uint formatCount = 0;
        khrSurfaceExt.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, null);
        SurfaceFormatKHR[] formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* p = formats)
            khrSurfaceExt.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, p);

        SurfaceFormatKHR surfaceFormat = formats[0];
        foreach (ref SurfaceFormatKHR f in formats.AsSpan())
        {
            if (f.Format == Format.B8G8R8A8Unorm && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            { surfaceFormat = f; break; }
        }

        // Choose present mode (FIFO is always available = VSync)
        uint modeCount = 0;
        khrSurfaceExt.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref modeCount, null);
        PresentModeKHR[] modes = new PresentModeKHR[modeCount];
        fixed (PresentModeKHR* p = modes)
            khrSurfaceExt.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref modeCount, p);

        PresentModeKHR presentMode = PresentModeKHR.FifoKhr; // VSync on
        if (!vSyncEnabled)
        {
            foreach (PresentModeKHR m in modes)
            {
                if (m == PresentModeKHR.MailboxKhr) { presentMode = m; break; } // prefer triple buffering
                if (m == PresentModeKHR.ImmediateKhr) presentMode = m; // fallback: uncapped
            }
        }

        // Choose swap extent
        Extent2D extent;
        if (caps.CurrentExtent.Width != uint.MaxValue)
        {
            extent = caps.CurrentExtent;
        }
        else
        {
            extent = new Extent2D
            {
                Width = Math.Clamp((uint)window.Size.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Height = Math.Clamp((uint)window.Size.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height),
            };
        }

        uint imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
            imageCount = caps.MaxImageCount;

        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default,
        };

        if (graphicsFamily != presentFamily)
        {
            uint* familyIndices = stackalloc uint[] { graphicsFamily, presentFamily };
            createInfo.ImageSharingMode = SharingMode.Concurrent;
            createInfo.QueueFamilyIndexCount = 2;
            createInfo.PQueueFamilyIndices = familyIndices;
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        Check(khrSwapchainExt.CreateSwapchain(device, in createInfo, null, out swapchain), "create swapchain");

        khrSwapchainExt.GetSwapchainImages(device, swapchain, ref imageCount, null);
        swapchainImages = new Image[imageCount];
        fixed (Image* p = swapchainImages)
            khrSwapchainExt.GetSwapchainImages(device, swapchain, ref imageCount, p);

        swapchainImageFormat = surfaceFormat.Format;
        swapchainExtent = extent;
    }

    // ── Image views ───────────────────────────────────────────────────────────

    private void CreateImageViews()
    {
        swapchainImageViews = new ImageView[swapchainImages.Length];
        for (int i = 0; i < swapchainImages.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = swapchainImageFormat,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };

            Check(vk.CreateImageView(device, in createInfo, null, out swapchainImageViews[i]), "create image view");
        }
    }

    // ── Render pass ───────────────────────────────────────────────────────────

    private void CreateRenderPass()
    {
        AttachmentDescription colorAttachment = new()
        {
            Format = swapchainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        AttachmentReference colorAttachmentRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };

        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        Check(vk.CreateRenderPass(device, in renderPassInfo, null, out renderPass), "create render pass");
    }

    // ── Graphics pipeline ─────────────────────────────────────────────────────

    private void CreateGraphicsPipeline()
    {
        byte[] vertCode = File.ReadAllBytes(ResolvePath("resources/shader.vert.spv"));
        byte[] fragCode = File.ReadAllBytes(ResolvePath("resources/shader.frag.spv"));

        ShaderModule vertModule = CreateShaderModule(vertCode);
        ShaderModule fragModule = CreateShaderModule(fragCode);

        byte* mainName = (byte*)SilkMarshal.StringToPtr("main");

        PipelineShaderStageCreateInfo vertStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertModule,
            PName = mainName,
        };

        PipelineShaderStageCreateInfo fragStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragModule,
            PName = mainName,
        };

        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[] { vertStage, fragStage };

        // Vertex input: Position (vec3) + TexCoord (vec2)
        VertexInputBindingDescription bindingDesc = new()
        {
            Binding = 0,
            Stride = 5 * sizeof(float),
            InputRate = VertexInputRate.Vertex,
        };

        VertexInputAttributeDescription* attrDescs = stackalloc VertexInputAttributeDescription[]
        {
            new() { Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Binding = 0, Location = 1, Format = Format.R32G32Sfloat, Offset = 3 * sizeof(float) },
        };

        PipelineVertexInputStateCreateInfo vertexInputInfo = new()
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &bindingDesc,
            VertexAttributeDescriptionCount = 2,
            PVertexAttributeDescriptions = attrDescs,
        };

        PipelineInputAssemblyStateCreateInfo inputAssembly = new()
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
            PrimitiveRestartEnable = false,
        };

        Viewport viewport = new()
        {
            X = 0, Y = 0,
            Width = swapchainExtent.Width, Height = swapchainExtent.Height,
            MinDepth = 0, MaxDepth = 1,
        };

        Rect2D scissor = new() { Offset = default, Extent = swapchainExtent };

        PipelineViewportStateCreateInfo viewportState = new()
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            PViewports = &viewport,
            ScissorCount = 1,
            PScissors = &scissor,
        };

        PipelineRasterizationStateCreateInfo rasterizer = new()
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = PolygonMode.Fill,
            LineWidth = 1,
            CullMode = CullModeFlags.BackBit,
            FrontFace = FrontFace.CounterClockwise,
            DepthBiasEnable = false,
        };

        PipelineMultisampleStateCreateInfo multisampling = new()
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable = false,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        PipelineColorBlendAttachmentState colorBlendAttachment = new()
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = false,
        };

        PipelineColorBlendStateCreateInfo colorBlending = new()
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };

        DescriptorSetLayout setLayout = descriptorSetLayout;
        PipelineLayoutCreateInfo pipelineLayoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
        };

        Check(vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out pipelineLayout), "create pipeline layout");

        GraphicsPipelineCreateInfo pipelineInfo = new()
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInputInfo,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PColorBlendState = &colorBlending,
            Layout = pipelineLayout,
            RenderPass = renderPass,
            Subpass = 0,
        };

        Check(vk.CreateGraphicsPipelines(device, default, 1, in pipelineInfo, null, out graphicsPipeline), "create graphics pipeline");

        SilkMarshal.Free((nint)mainName);
        vk.DestroyShaderModule(device, vertModule, null);
        vk.DestroyShaderModule(device, fragModule, null);
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* codePtr = code)
        {
            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)codePtr,
            };
            Check(vk.CreateShaderModule(device, in createInfo, null, out ShaderModule module), "create shader module");
            return module;
        }
    }

    // ── Framebuffers ──────────────────────────────────────────────────────────

    private void CreateFramebuffers()
    {
        framebuffers = new Framebuffer[swapchainImageViews.Length];
        for (int i = 0; i < swapchainImageViews.Length; i++)
        {
            ImageView attachment = swapchainImageViews[i];
            FramebufferCreateInfo fbInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = swapchainExtent.Width,
                Height = swapchainExtent.Height,
                Layers = 1,
            };
            Check(vk.CreateFramebuffer(device, in fbInfo, null, out framebuffers[i]), "create framebuffer");
        }
    }

    // ── Descriptor set layout ─────────────────────────────────────────────────

    private void CreateDescriptorSetLayout()
    {
        DescriptorSetLayoutBinding samplerBinding = new()
        {
            Binding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &samplerBinding,
        };

        Check(vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out descriptorSetLayout), "create descriptor set layout");
    }

    // ── Texture image, view, sampler, staging buffer ──────────────────────────

    private void CreateTextureResources()
    {
        // Texture image (DEVICE_LOCAL)
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D { Width = textureWidth, Height = textureHeight, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.R8G8B8A8Unorm,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit,
        };

        Check(vk.CreateImage(device, in imageInfo, null, out textureImage), "create texture image");

        vk.GetImageMemoryRequirements(device, textureImage, out MemoryRequirements memReqs);
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = FindMemoryType(memReqs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(vk.AllocateMemory(device, in allocInfo, null, out textureImageMemory), "allocate texture memory");
        Check(vk.BindImageMemory(device, textureImage, textureImageMemory, 0), "bind texture memory");

        // Image view
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = textureImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0, LevelCount = 1,
                BaseArrayLayer = 0, LayerCount = 1,
            },
        };
        Check(vk.CreateImageView(device, in viewInfo, null, out textureImageView), "create texture image view");

        // Sampler (Linear min, Nearest mag — same as GL version)
        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MipmapMode = SamplerMipmapMode.Nearest,
            MaxLod = 0,
        };
        Check(vk.CreateSampler(device, in samplerInfo, null, out textureSampler), "create texture sampler");

        // Staging buffer (HOST_VISIBLE | HOST_COHERENT, persistently mapped)
        CreateBufferWithMemory(stagingBufferSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out stagingBuffer, out stagingBufferMemory);

        void* mapped;
        vk.MapMemory(device, stagingBufferMemory, 0, stagingBufferSize, 0, &mapped);
        stagingBufferMapped = mapped;
        // Zero-initialize so first frames before rasterizer starts show black
        Unsafe.InitBlockUnaligned(ref Unsafe.AsRef<byte>(stagingBufferMapped), 0, (uint)stagingBufferSize);

        // Initial transition to SHADER_READ_ONLY so the first draw doesn't read undefined data
        TransitionImageLayout(textureImage, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal);
    }

    // ── Descriptor pool & set ─────────────────────────────────────────────────

    private void CreateDescriptorPoolAndSet()
    {
        DescriptorPoolSize poolSize = new()
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
        };

        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 1,
        };

        Check(vk.CreateDescriptorPool(device, in poolInfo, null, out descriptorPool), "create descriptor pool");

        DescriptorSetLayout layout = descriptorSetLayout;
        DescriptorSetAllocateInfo setAllocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };

        Check(vk.AllocateDescriptorSets(device, in setAllocInfo, out descriptorSet), "allocate descriptor set");

        DescriptorImageInfo imageDescInfo = new()
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = textureImageView,
            Sampler = textureSampler,
        };

        WriteDescriptorSet descriptorWrite = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageDescInfo,
        };

        vk.UpdateDescriptorSets(device, 1, in descriptorWrite, 0, null);
    }

    // ── Frame upload ──────────────────────────────────────────────────────────

    /// <summary>Copy CPU-rasterized frame pixels into the staging buffer. Call before DrawFrame.</summary>
    public void UploadFrame(uint* pixels, int pixelCount)
    {
        System.Buffer.MemoryCopy(pixels, stagingBufferMapped, stagingBufferSize, (ulong)pixelCount * sizeof(uint));
        frameIsCompressed = false;
    }

    /// <summary>Copy compressed blocks and set up compute dispatch. Call after BeginFrame, before DrawFrame.</summary>
    public void UploadCompressedFrame(uint* blocks, int blockCount, uint groupsX, uint groupsY)
    {
        void* dst = compressedBlocksMappedPtrs[currentFrame];
        ulong byteCount = (ulong)blockCount * sizeof(uint);
        System.Buffer.MemoryCopy(blocks, dst, compressedBlocksBufferSize, byteCount);
        // Zero past the data — the dispatch overshoots (blockCount * local_size_x invocations),
        // and stale data would cause corruption.
        if (byteCount < compressedBlocksBufferSize)
            Unsafe.InitBlockUnaligned(ref Unsafe.AddByteOffset(ref Unsafe.AsRef<byte>(dst), (nint)byteCount),
                0, (uint)(compressedBlocksBufferSize - byteCount));
        frameIsCompressed = true;
        // groupsX = total block count. The shader has local_size_x = 32,
        // so we need ceil(blockCount / 32) work groups, not blockCount work groups.
        computeGroupsX = (groupsX + 31) / 32;
        computeGroupsY = groupsY;
    }

    // ── Compute pipeline (frame decompression) ───────────────────────────────

    /// <summary>Initialize compute pipeline for GPU frame decompression. Call once after construction.</summary>
    public void InitComputePipeline(string shaderName)
    {
        int frameCount = swapchainImages.Length;

        // Per-frame storage buffers — worst case varies by compressor:
        // RLE: ~frame + frame/64, Bl16: frame + frame/16 + chunk allocation overhead
        compressedBlocksBufferSize = stagingBufferSize + stagingBufferSize / 4;
        compressedBlocksBuffers = new Buffer[frameCount];
        compressedBlocksMemories = new DeviceMemory[frameCount];
        compressedBlocksMappedPtrs = new void*[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            CreateBufferWithMemory(compressedBlocksBufferSize, BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out compressedBlocksBuffers[i], out compressedBlocksMemories[i]);
            void* mapped;
            vk.MapMemory(device, compressedBlocksMemories[i], 0, compressedBlocksBufferSize, 0, &mapped);
            compressedBlocksMappedPtrs[i] = mapped;
        }

        // Compute descriptor set layout: binding 0 = storage buffer, binding 1 = storage image
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, StageFlags = ShaderStageFlags.ComputeBit },
            new() { Binding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, StageFlags = ShaderStageFlags.ComputeBit },
        };
        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        Check(vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out computeDescriptorSetLayout), "create compute descriptor set layout");

        // Push constants: Width + Height (2 uints)
        PushConstantRange pushRange = new()
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = 2 * sizeof(uint),
        };
        DescriptorSetLayout cLayout = computeDescriptorSetLayout;
        PipelineLayoutCreateInfo plInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &cLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        Check(vk.CreatePipelineLayout(device, in plInfo, null, out computePipelineLayout), "create compute pipeline layout");

        // Compute pipeline
        string spvPath = shaderName.EndsWith(".spv") ? shaderName : shaderName + ".spv";
        byte[] compCode = File.ReadAllBytes(ResolvePath("resources/" + spvPath));
        ShaderModule compModule = CreateShaderModule(compCode);

        byte* mainName = (byte*)SilkMarshal.StringToPtr("main");
        PipelineShaderStageCreateInfo stageInfo = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = compModule,
            PName = mainName,
        };

        ComputePipelineCreateInfo pipelineInfo = new()
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stageInfo,
            Layout = computePipelineLayout,
        };
        Check(vk.CreateComputePipelines(device, default, 1, in pipelineInfo, null, out computePipeline), "create compute pipeline");

        SilkMarshal.Free((nint)mainName);
        vk.DestroyShaderModule(device, compModule, null);

        // Per-frame descriptor pool + sets (each set binds a different storage buffer)
        DescriptorPoolSize* poolSizes = stackalloc DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.StorageBuffer, DescriptorCount = (uint)frameCount },
            new() { Type = DescriptorType.StorageImage, DescriptorCount = (uint)frameCount },
        };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
            MaxSets = (uint)frameCount,
        };
        Check(vk.CreateDescriptorPool(device, in poolInfo, null, out computeDescriptorPool), "create compute descriptor pool");

        computeDescriptorSets = new DescriptorSet[frameCount];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < frameCount; i++)
        {
            DescriptorSetAllocateInfo setAlloc = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = computeDescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &cLayout,
            };
            Check(vk.AllocateDescriptorSets(device, in setAlloc, out computeDescriptorSets[i]), "allocate compute descriptor set");

            DescriptorBufferInfo bufInfo = new() { Buffer = compressedBlocksBuffers[i], Offset = 0, Range = compressedBlocksBufferSize };
            DescriptorImageInfo imgInfo = new() { ImageView = textureImageView, ImageLayout = ImageLayout.General };

            writes[0] = new()
            {
                SType = StructureType.WriteDescriptorSet, DstSet = computeDescriptorSets[i],
                DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bufInfo,
            };
            writes[1] = new()
            {
                SType = StructureType.WriteDescriptorSet, DstSet = computeDescriptorSets[i],
                DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &imgInfo,
            };
            vk.UpdateDescriptorSets(device, 2, writes, 0, null);
        }

        hasComputePipeline = true;
    }

    private void TransitionImageLayout(Image image, ImageLayout oldLayout, ImageLayout newLayout)
    {
        CommandBufferAllocateInfo cmdAllocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1,
        };
        vk.AllocateCommandBuffers(device, in cmdAllocInfo, out CommandBuffer cmd);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        vk.BeginCommandBuffer(cmd, in beginInfo);

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0, LevelCount = 1,
                BaseArrayLayer = 0, LayerCount = 1,
            },
        };

        PipelineStageFlags srcStage, dstStage;
        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }
        else // UNDEFINED → SHADER_READ_ONLY (initial)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }

        vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, in barrier);
        vk.EndCommandBuffer(cmd);

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        vk.QueueSubmit(graphicsQueue, 1, in submitInfo, default);
        vk.QueueWaitIdle(graphicsQueue);
        vk.FreeCommandBuffers(device, commandPool, 1, in cmd);
    }

    // ── Command pool ──────────────────────────────────────────────────────────

    private void CreateCommandPool()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = graphicsFamily,
        };
        Check(vk.CreateCommandPool(device, in poolInfo, null, out commandPool), "create command pool");
    }

    // ── Vertex & index buffers ────────────────────────────────────────────────

    private void CreateVertexBuffer()
    {
        ulong size = (ulong)(Vertices.Length * sizeof(float));

        CreateBufferWithMemory(size, BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit, out vertexBuffer, out vertexBufferMemory);

        CreateBufferWithMemory(size, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer staging, out DeviceMemory stagingMem);

        void* data;
        vk.MapMemory(device, stagingMem, 0, size, 0, &data);
        fixed (float* src = Vertices)
            System.Buffer.MemoryCopy(src, data, size, size);
        vk.UnmapMemory(device, stagingMem);

        CopyBuffer(staging, vertexBuffer, size);

        vk.DestroyBuffer(device, staging, null);
        vk.FreeMemory(device, stagingMem, null);
    }

    private void CreateIndexBuffer()
    {
        ulong size = (ulong)(Indices.Length * sizeof(uint));

        CreateBufferWithMemory(size, BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit, out indexBuffer, out indexBufferMemory);

        CreateBufferWithMemory(size, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer staging, out DeviceMemory stagingMem);

        void* data;
        vk.MapMemory(device, stagingMem, 0, size, 0, &data);
        fixed (uint* src = Indices)
            System.Buffer.MemoryCopy(src, data, size, size);
        vk.UnmapMemory(device, stagingMem);

        CopyBuffer(staging, indexBuffer, size);

        vk.DestroyBuffer(device, staging, null);
        vk.FreeMemory(device, stagingMem, null);
    }

    private void CreateBufferWithMemory(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties,
        out Buffer buffer, out DeviceMemory memory)
    {
        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Check(vk.CreateBuffer(device, in bufferInfo, null, out buffer), "create buffer");

        vk.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements memReqs);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = FindMemoryType(memReqs.MemoryTypeBits, properties),
        };
        Check(vk.AllocateMemory(device, in allocInfo, null, out memory), "allocate buffer memory");
        Check(vk.BindBufferMemory(device, buffer, memory, 0), "bind buffer memory");
    }

    private void CopyBuffer(Buffer src, Buffer dst, ulong size)
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1,
        };

        vk.AllocateCommandBuffers(device, in allocInfo, out CommandBuffer cmd);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        vk.BeginCommandBuffer(cmd, in beginInfo);

        BufferCopy copyRegion = new() { Size = size };
        vk.CmdCopyBuffer(cmd, src, dst, 1, in copyRegion);

        vk.EndCommandBuffer(cmd);

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        vk.QueueSubmit(graphicsQueue, 1, in submitInfo, default);
        vk.QueueWaitIdle(graphicsQueue);

        vk.FreeCommandBuffers(device, commandPool, 1, in cmd);
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        }
        throw new Exception($"Failed to find suitable memory type (filter={typeFilter:X}, props={properties})");
    }

    // ── Command buffers ───────────────────────────────────────────────────────

    private void CreateCommandBuffers()
    {
        commandBuffers = new CommandBuffer[swapchainImages.Length];
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)commandBuffers.Length,
        };
        fixed (CommandBuffer* p = commandBuffers)
            Check(vk.AllocateCommandBuffers(device, in allocInfo, p), "allocate command buffers");
    }

    // ── Sync objects ──────────────────────────────────────────────────────────

    private void CreateSyncObjects()
    {
        // Allocate per swapchain image to avoid semaphore reuse while presentation is still pending
        int count = swapchainImages.Length;
        imageAvailableSemaphores = new Semaphore[count];
        renderFinishedSemaphores = new Semaphore[count];
        inFlightFences = new Fence[count];

        SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit, // start signaled so first WaitForFences doesn't block
        };

        for (int i = 0; i < count; i++)
        {
            Check(vk.CreateSemaphore(device, in semInfo, null, out imageAvailableSemaphores[i]), "create semaphore");
            Check(vk.CreateSemaphore(device, in semInfo, null, out renderFinishedSemaphores[i]), "create semaphore");
            Check(vk.CreateFence(device, in fenceInfo, null, out inFlightFences[i]), "create fence");
        }
    }

    // ── Draw frame ────────────────────────────────────────────────────────────

    /// <summary>Wait for the GPU to finish with the current frame slot's resources. Call before UploadFrame/UploadCompressedFrame.</summary>
    public void BeginFrame()
    {
        Fence fence = inFlightFences[currentFrame];
        vk.WaitForFences(device, 1, in fence, true, ulong.MaxValue);
    }

    public void DrawFrame()
    {
        Fence fence = inFlightFences[currentFrame];

        uint imageIndex;
        Result acquireResult = khrSwapchainExt.AcquireNextImage(device, swapchain, ulong.MaxValue,
            imageAvailableSemaphores[currentFrame], default, &imageIndex);

        if (acquireResult == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return;
        }

        vk.ResetFences(device, 1, in fence);

        CommandBuffer cmd = commandBuffers[currentFrame];
        vk.ResetCommandBuffer(cmd, 0);
        RecordCommandBuffer(cmd, imageIndex);

        Semaphore waitSemaphore = imageAvailableSemaphores[currentFrame];
        Semaphore signalSemaphore = renderFinishedSemaphores[currentFrame];
        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        Check(vk.QueueSubmit(graphicsQueue, 1, in submitInfo, fence), "submit draw command buffer");

        SwapchainKHR sc = swapchain;
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &sc,
            PImageIndices = &imageIndex,
        };

        Result presentResult = khrSwapchainExt.QueuePresent(presentQueue, in presentInfo);
        if (presentResult == Result.ErrorOutOfDateKhr || presentResult == Result.SuboptimalKhr)
            RecreateSwapchain();

        currentFrame = (currentFrame + 1) % swapchainImages.Length;
    }

    private void RecordCommandBuffer(CommandBuffer cmd, uint imageIndex)
    {
        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
        Check(vk.BeginCommandBuffer(cmd, in beginInfo), "begin command buffer");

        ImageSubresourceRange subresRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1,
        };

        if (frameIsCompressed && hasComputePipeline)
        {
            // ── Compute decompression path ────────────────────────────────
            // Transition texture to GENERAL for compute write, preserving previous content.
            // The compute shader skips suppressed pixels (padding at slice boundaries),
            // so we must NOT use Undefined (which discards content on tiled GPUs like Apple Silicon).
            ImageMemoryBarrier toGeneral = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = textureImage,
                SubresourceRange = subresRange,
                SrcAccessMask = AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.ShaderWriteBit,
            };
            vk.CmdPipelineBarrier(cmd, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.ComputeShaderBit,
                0, 0, null, 0, null, 1, in toGeneral);

            // Bind compute pipeline and descriptors
            vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, computePipeline);
            DescriptorSet cds = computeDescriptorSets[currentFrame];
            vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, computePipelineLayout, 0, 1, &cds, 0, null);

            // Push constants: Width/Height are swapped (column-major texture)
            uint* pushData = stackalloc uint[] { textureWidth, textureHeight };
            vk.CmdPushConstants(cmd, computePipelineLayout, ShaderStageFlags.ComputeBit, 0, 2 * sizeof(uint), pushData);

            vk.CmdDispatch(cmd, computeGroupsX, computeGroupsY, 1);

            // Barrier: compute write → fragment read
            ImageMemoryBarrier toShaderRead = toGeneral;
            toShaderRead.OldLayout = ImageLayout.General;
            toShaderRead.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            toShaderRead.SrcAccessMask = AccessFlags.ShaderWriteBit;
            toShaderRead.DstAccessMask = AccessFlags.ShaderReadBit;
            vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.FragmentShaderBit,
                0, 0, null, 0, null, 1, in toShaderRead);
        }
        else
        {
            // ── Direct upload path (staging buffer → texture) ─────────────
            ImageMemoryBarrier toTransfer = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = textureImage,
                SubresourceRange = subresRange,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
            };
            vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, in toTransfer);

            BufferImageCopy region = new()
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1,
                },
                ImageOffset = default,
                ImageExtent = new Extent3D { Width = textureWidth, Height = textureHeight, Depth = 1 },
            };
            vk.CmdCopyBufferToImage(cmd, stagingBuffer, textureImage, ImageLayout.TransferDstOptimal, 1, in region);

            ImageMemoryBarrier toShaderRead = toTransfer;
            toShaderRead.OldLayout = ImageLayout.TransferDstOptimal;
            toShaderRead.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            toShaderRead.SrcAccessMask = AccessFlags.TransferWriteBit;
            toShaderRead.DstAccessMask = AccessFlags.ShaderReadBit;
            vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
                0, 0, null, 0, null, 1, in toShaderRead);
        }

        // ── Render pass ───────────────────────────────────────────────────
        ClearValue clearColor = new(new ClearColorValue(0.0f, 0.0f, 0.0f, 1.0f));

        RenderPassBeginInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffers[imageIndex],
            RenderArea = new Rect2D { Offset = default, Extent = swapchainExtent },
            ClearValueCount = 1,
            PClearValues = &clearColor,
        };

        vk.CmdBeginRenderPass(cmd, in renderPassInfo, SubpassContents.Inline);
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, graphicsPipeline);

        DescriptorSet ds = descriptorSet;
        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, &ds, 0, null);

        Buffer vb = vertexBuffer;
        ulong offset = 0;
        vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);
        vk.CmdBindIndexBuffer(cmd, indexBuffer, 0, IndexType.Uint32);
        vk.CmdDrawIndexed(cmd, (uint)Indices.Length, 1, 0, 0, 0);

        vk.CmdEndRenderPass(cmd);
        Check(vk.EndCommandBuffer(cmd), "record command buffer");
    }

    // ── Swapchain recreation ──────────────────────────────────────────────────

    private void RecreateSwapchain()
    {
        // Wait until window has non-zero size
        while (window.Size.X == 0 || window.Size.Y == 0)
            window.DoEvents();

        vk.DeviceWaitIdle(device);

        int oldImageCount = swapchainImages.Length;

        CleanupSwapchain();

        CreateSwapchain();
        CreateImageViews();
        CreateFramebuffers();

        // Recreate per-image resources if swapchain image count changed
        if (swapchainImages.Length != oldImageCount)
        {
            for (int i = 0; i < oldImageCount; i++)
            {
                vk.DestroySemaphore(device, imageAvailableSemaphores[i], null);
                vk.DestroySemaphore(device, renderFinishedSemaphores[i], null);
                vk.DestroyFence(device, inFlightFences[i], null);
            }
            vk.FreeCommandBuffers(device, commandPool, (uint)oldImageCount, commandBuffers);

            CreateCommandBuffers();
            CreateSyncObjects();
        }

        currentFrame = 0;
    }

    private void CleanupSwapchain()
    {
        foreach (Framebuffer fb in framebuffers)
            vk.DestroyFramebuffer(device, fb, null);
        foreach (ImageView iv in swapchainImageViews)
            vk.DestroyImageView(device, iv, null);
        khrSwapchainExt.DestroySwapchain(device, swapchain, null);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        vk.DeviceWaitIdle(device);

        for (int i = 0; i < imageAvailableSemaphores.Length; i++)
        {
            vk.DestroySemaphore(device, imageAvailableSemaphores[i], null);
            vk.DestroySemaphore(device, renderFinishedSemaphores[i], null);
            vk.DestroyFence(device, inFlightFences[i], null);
        }

        vk.DestroyCommandPool(device, commandPool, null);

        CleanupSwapchain();

        vk.DestroyBuffer(device, indexBuffer, null);
        vk.FreeMemory(device, indexBufferMemory, null);
        vk.DestroyBuffer(device, vertexBuffer, null);
        vk.FreeMemory(device, vertexBufferMemory, null);

        vk.UnmapMemory(device, stagingBufferMemory);
        vk.DestroyBuffer(device, stagingBuffer, null);
        vk.FreeMemory(device, stagingBufferMemory, null);

        vk.DestroySampler(device, textureSampler, null);
        vk.DestroyImageView(device, textureImageView, null);
        vk.DestroyImage(device, textureImage, null);
        vk.FreeMemory(device, textureImageMemory, null);

        vk.DestroyDescriptorPool(device, descriptorPool, null);
        vk.DestroyDescriptorSetLayout(device, descriptorSetLayout, null);

        if (hasComputePipeline)
        {
            for (int i = 0; i < compressedBlocksBuffers.Length; i++)
            {
                vk.UnmapMemory(device, compressedBlocksMemories[i]);
                vk.DestroyBuffer(device, compressedBlocksBuffers[i], null);
                vk.FreeMemory(device, compressedBlocksMemories[i], null);
            }
            vk.DestroyDescriptorPool(device, computeDescriptorPool, null);
            vk.DestroyDescriptorSetLayout(device, computeDescriptorSetLayout, null);
            vk.DestroyPipeline(device, computePipeline, null);
            vk.DestroyPipelineLayout(device, computePipelineLayout, null);
        }

        vk.DestroyPipeline(device, graphicsPipeline, null);
        vk.DestroyPipelineLayout(device, pipelineLayout, null);
        vk.DestroyRenderPass(device, renderPass, null);

        vk.DestroyDevice(device, null);
        khrSurfaceExt.DestroySurface(instance, surface, null);
        vk.DestroyInstance(instance, null);
        vk.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolvePath(string relativePath)
    {
        return Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(AppContext.BaseDirectory, relativePath);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new Exception($"Vulkan error during {operation}: {result}");
    }
}
