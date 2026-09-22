# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

- `dotnet build` - Build the solution
- `dotnet test` - Run all tests
- `dotnet test --filter "FullyQualifiedName~TestName"` - Run a specific test

## Project Overview

ktsu.Invoker is a .NET library for thread-safe delegate execution, designed for scenarios where code must run on a specific thread (UI threads, OpenGL/DirectX contexts, etc.).

## Architecture

The library consists of a single class `Invoker` in `Invoker/Invoker.cs`:

- Created on an "owner thread" and captures that thread's ID
- Maintains a `ConcurrentQueue<Task>` for cross-thread invocations
- When `Invoke`/`InvokeAsync` is called from the owner thread, executes immediately
- When called from other threads, queues the task and blocks/awaits until `DoInvokes()` processes it
- `DoInvokes()` must be called on the owner thread (typically in a main loop) to process queued tasks

## Solution Structure

- **Invoker/** - Main library (multi-targets via ktsu.Sdk)
- **Invoker.Test/** - MSTest unit tests (targets net10.0 only)
- **Invoker.Demo/** - Console app demonstrating usage (not packable)

## SDK Configuration

Projects use `ktsu.Sdk` which provides centralized build configuration. Package versions are managed centrally in `Directory.Packages.props`.

`ktsu.Sdk` derives each project's assembly name and package ID from its solution-relative folder path, so a project folder is an identity claim: `Invoker.Demo/` produces `ktsu.Invoker.Demo`, while a folder without the family prefix would claim a name this repository does not own. `ProjectNamingTests` fails the build if a project folder, its `.csproj` filename, or its solution entry stops agreeing with that convention.
