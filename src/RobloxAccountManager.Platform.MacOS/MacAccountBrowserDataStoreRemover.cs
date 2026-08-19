using System.Runtime.InteropServices;
using Contracts = RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.MacOS;

/// <summary>
/// Removes exactly one account's persistent WKWebsiteDataStore through the macOS 14 API. The
/// Avalonia layer must dispose/detach the corresponding WKWebView before calling this service.
/// No filesystem glob or shared WebKit directory is ever deleted.
/// </summary>
public sealed partial class MacAccountBrowserDataStoreRemover : Contracts.IAccountBrowserDataStoreRemover
{
    public bool IsSupported => OperatingSystem.IsMacOSVersionAtLeast(14);

    public async ValueTask RemoveAsync(Guid dataStoreIdentifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            throw new PlatformNotSupportedException("platform-not-supported: persistent WKWebsiteDataStore identifiers require macOS 14 or newer.");
        }

        if (dataStoreIdentifier == Guid.Empty)
        {
            throw new ArgumentException("A non-empty account data-store identifier is required.", nameof(dataStoreIdentifier));
        }

        var result = await NativeMethods.RemoveExactStoreAsync(dataStoreIdentifier)
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        if (result == 0)
        {
            return;
        }

        throw new InvalidOperationException(result switch
        {
            1 => "platform-not-supported: the requested WKWebsiteDataStore identifier was unavailable.",
            _ => "platform-data-store-removal-failed"
        });
    }

    private static partial class NativeMethods
    {
        // WKWebsiteDataStore's macOS 14 API is Objective-C based. Passing the exact NSUUID to
        // +dataStoreForIdentifier: avoids touching unrelated accounts or WebKit shared storage.
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr GetClass(string name);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr RegisterSelector(string name);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static partial IntPtr Send0(IntPtr receiver, IntPtr selector);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static partial IntPtr Send1(IntPtr receiver, IntPtr selector, IntPtr argument);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static partial IntPtr Send2(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr FindSymbol(IntPtr handle, string symbol);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CompletionCallback(IntPtr block, IntPtr error);

        private static readonly CompletionCallback Completion = CompleteRemoval;
        private static readonly IntPtr CompletionPointer = Marshal.GetFunctionPointerForDelegate(Completion);
        private static readonly IntPtr DescriptorPointer = CreateDescriptor();

        internal static Task<int> RemoveExactStoreAsync(Guid identifier)
        {
            var stringClass = GetClass("NSString");
            var alloc = RegisterSelector("alloc");
            var initWithUtf8 = RegisterSelector("initWithUTF8String:");
            var utf8 = Marshal.StringToCoTaskMemUTF8(identifier.ToString("D"));
            var nsString = Send1(Send0(stringClass, alloc), initWithUtf8, utf8);
            if (nsString == IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(utf8);
                return Task.FromResult(2);
            }

            try
            {
                var uuid = Send1(GetClass("NSUUID"), RegisterSelector("UUIDWithUUIDString:"), nsString);
                if (uuid == IntPtr.Zero)
                {
                    return Task.FromResult(2);
                }

                var blockClass = FindSymbol(new IntPtr(-2), "_NSConcreteStackBlock");
                if (blockClass == IntPtr.Zero)
                {
                    return Task.FromResult(2);
                }

                var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var completionHandle = GCHandle.Alloc(completion);
                var literal = new BlockLiteral
                {
                    Isa = blockClass,
                    Flags = 0,
                    Reserved = 0,
                    Invoke = CompletionPointer,
                    Descriptor = DescriptorPointer,
                    Context = GCHandle.ToIntPtr(completionHandle)
                };
                var block = Marshal.AllocHGlobal(Marshal.SizeOf<BlockLiteral>());
                Marshal.StructureToPtr(literal, block, false);
                try
                {
                    _ = Send2(
                        GetClass("WKWebsiteDataStore"),
                        RegisterSelector("removeDataStoreForIdentifier:completionHandler:"),
                        uuid,
                        block);
                }
                catch
                {
                    completionHandle.Free();
                    throw;
                }
                finally
                {
                    // WebKit copies the stack block before objc_msgSend returns.
                    Marshal.FreeHGlobal(block);
                }
                return completion.Task;
            }
            catch (EntryPointNotFoundException)
            {
                return Task.FromResult(1);
            }
            finally
            {
                _ = Send0(nsString, RegisterSelector("release"));
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        private static void CompleteRemoval(IntPtr block, IntPtr error)
        {
            var literal = Marshal.PtrToStructure<BlockLiteral>(block);
            if (literal.Context == IntPtr.Zero) return;
            var handle = GCHandle.FromIntPtr(literal.Context);
            try
            {
                if (handle.Target is TaskCompletionSource<int> completion)
                    completion.TrySetResult(error == IntPtr.Zero ? 0 : 2);
            }
            finally
            {
                handle.Free();
            }
        }

        private static IntPtr CreateDescriptor()
        {
            var descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<BlockDescriptor>());
            Marshal.StructureToPtr(new BlockDescriptor
            {
                Reserved = 0,
                Size = (nuint)Marshal.SizeOf<BlockLiteral>()
            }, descriptor, false);
            return descriptor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlockLiteral
        {
            public IntPtr Isa;
            public int Flags;
            public int Reserved;
            public IntPtr Invoke;
            public IntPtr Descriptor;
            public IntPtr Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlockDescriptor
        {
            public nuint Reserved;
            public nuint Size;
        }
    }
}
