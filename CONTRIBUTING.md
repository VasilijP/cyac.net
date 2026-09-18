# Contributing

Thanks for looking. This is a port of a 1991 game to C# / .NET 10; the code is the interesting part,
and the rules below are the few that are not obvious.

## Build and run

```sh
mkdir game                                  # a fresh clone has no game/ folder; this is the drop zone
dotnet build -c Release
dotnet run --project src/CYAC.Port.Host -c Release -- --preflight    # the start-up check, on the console
```

There are no test projects here; Say in the pull request how you checked the change — that is the signal in their place.

## The one hard rule: no original data in the source tree

The repository contains no game data, and it must stay that way. The port reads the **data tree** the
transform builds from your own copy of the game — never the original files, and never a copy of their
contents pasted into a source file.

A guard enforces this before anything is published. It runs in the reverse-engineering project, where
the originals are, and nothing reaches this repository until it is green: it compares every published
file against the originals and fails when it finds

- five or more consecutive words of the original's text,
- twelve or more consecutive bytes of the original's data, or
- a path that only exists on somebody's own machine.

Short quotations that a reader needs in order to check a claim — the instructions a routine is decoded
from, a record's layout, a module's entry-point offsets — are allowed, but each one is reviewed and
recorded with its reason in the guard's exemption registry. You cannot run the guard from here, so if
a change of yours quotes the original at all, say so in the pull request: the answer is almost always
to state the fact in prose, or to read the value from the data tree, rather than to add an exemption.

Constants and indices the port computes with stay in code. Sentences, tables and artwork come out of
the extraction at run time.

## Generated files

Some files in the tree are generated and should not be hand-edited; each says so at the top
(`src/CYAC.Port.Core/Schema/state_schema.json` is the largest of them). If you need one changed,
open an issue saying which and why.

## Style

- Nullable and warnings-as-errors are on; keep them green.
- One thought per comment: what the member is, and why it is that way. Where a claim is about the
  original program, cite it — `image@0x1234`, or `asset:<archive>/<NAME>@0x12`.
- If your fork adds tests, name them as sentences (`TheWalkUpRecognisesACheckoutByEitherDropZone`),
  and let a test that cannot reach what it needs be *skipped* rather than quietly passed.

## Pull requests

Small and focused travels fastest. Say what you changed, how you checked it, and on which platform —
CI builds and smoke-runs on Windows, Linux and macOS, without any game files, so your account of what
you ran is the rest of the evidence.
