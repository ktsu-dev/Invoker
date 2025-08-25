// Copyright (c) ktsu.dev
// All rights reserved.
// Licensed under the MIT license.

#pragma warning disable IDE0161 //IDE0161: Convert to file-scoped namespace

#if NETSTANDARD2_0 || NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
	/// <summary>
	/// Reserved to be used by the compiler for tracking metadata.
	/// This class should not be used by developers in source code.
	/// </summary>
	internal static class IsExternalInit
	{
	}
}
#endif

namespace ktsu.Invoker
{
#if !NET6_0_OR_GREATER
	using System;
#endif

#if !NET6_0_OR_GREATER
	/// <summary>
	/// Polyfill for ArgumentNullException.ThrowIfNull for older .NET versions
	/// </summary>
	internal static class ArgumentNullExceptionPolyfill
	{
		/// <summary>
		/// Throws an <see cref="ArgumentNullException"/> if <paramref name="argument"/> is null.
		/// </summary>
		/// <param name="argument">The reference type argument to validate as non-null.</param>
		/// <param name="paramName">The name of the parameter with which <paramref name="argument"/> corresponds.</param>
		public static void ThrowIfNull(object? argument, string? paramName = null)
		{
			if (argument is null)
			{
				throw new ArgumentNullException(paramName);
			}
		}
	}
#endif
}
#pragma warning restore IDE0161
