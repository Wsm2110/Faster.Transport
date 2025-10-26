using System;
using System.Threading.Tasks;

namespace Faster.Transport.Primitives
{
    /// <summary>
    /// Provides backward-compatible helpers for Task/ValueTask completion
    /// across different .NET target frameworks.
    /// </summary>
    internal static class TaskCompat
    {
        /// <summary>
        /// A completed <see cref="Task"/>, compatible with all frameworks.
        /// </summary>
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        public static Task CompletedTask => Task.CompletedTask;
#else
        public static Task CompletedTask { get; } = Task.FromResult(true);
#endif

        /// <summary>
        /// A completed <see cref="ValueTask"/>, compatible with all frameworks.
        /// </summary>
#if NET5_0_OR_GREATER
        public static ValueTask CompletedValueTask => ValueTask.CompletedTask;
#else
        public static ValueTask CompletedValueTask => default;
#endif

        /// <summary>
        /// Returns a completed <see cref="Task{TResult}"/> with a given result.
        /// </summary>
        public static Task<TResult> FromResult<TResult>(TResult result)
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER
            => Task.FromResult(result);
#else
            => TaskEx.FromResult(result); // fallback if needed
#endif
    }
}
