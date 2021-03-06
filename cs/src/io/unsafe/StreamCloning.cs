// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Bond.IO.Unsafe
{
    using System;
    using System.ComponentModel;
    using System.IO;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Security;
    using Microsoft.Win32.SafeHandles;

    internal static class StreamCloning
    {
        delegate void GetOriginAndLength(MemoryStream stream, out int origin, out int length);
        private static readonly GetOriginAndLength getOriginAndLength;

        static readonly FileShare[] shareModes =
        {
            FileShare.Read, FileShare.ReadWrite, FileShare.ReadWrite | FileShare.Delete
        };

        static StreamCloning()
        {
            // MemoryStream can be created from byte[] at non-zero origin offset which
            // is not exposed via public APIs.
            // TODO: handle the case when the method API is not available (e.g. copy buffer)
            var internalGetOriginAndLength = typeof(MemoryStream).GetMethod(
                "InternalGetOriginAndLength", BindingFlags.NonPublic | BindingFlags.Instance);

            var stream = Expression.Parameter(typeof(MemoryStream));
            var origin = Expression.Parameter(typeof(int).MakeByRefType());
            var length = Expression.Parameter(typeof(int).MakeByRefType());
            getOriginAndLength = Expression.Lambda<GetOriginAndLength>(
                Expression.Call(stream, internalGetOriginAndLength, origin, length), stream, origin, length)
                .Compile();
        }

        public static Stream Clone(this Stream stream)
        {
            Stream clone = null;

            var memoryStream = stream as MemoryStream;
            if (memoryStream != null)
            {
                return Clone(memoryStream);
            }

            var fileStream = stream as FileStream;
            if (fileStream != null)
            {
                return Clone(fileStream);
            }

            var cloneable = stream as ICloneable;
            if (cloneable != null)
            {
                clone = cloneable.Clone() as Stream;
            }

            if (clone == null)
            {
                throw new NotSupportedException("Stream type " + stream.GetType().FullName + " can't be cloned.");
            }

            return clone;
        }
        
        static MemoryStream Clone(MemoryStream stream)
        {
            int origin, length;
            getOriginAndLength(stream, out origin, out length);
            return new MemoryStream(stream.GetBuffer(), origin, length - origin, false, true) { Position = stream.Position };
        }
        
        static FileStream Clone(FileStream stream)
        {
            var error = 0;
            foreach (var mode in shareModes)
            {
                var handle = NativeMethods.ReOpenFile(stream.SafeFileHandle, NativeMethods.FILE_GENERIC_READ, mode, 0);
                error = Marshal.GetLastWin32Error();

                if (error == NativeMethods.NO_ERROR)
                {
                    return new FileStream(handle, FileAccess.Read) { Position = stream.Position };
                }

                if (error != NativeMethods.ERROR_SHARING_VIOLATION)
                {
                    break;
                }
            }

            throw new Win32Exception(error);
        }
        
        static class NativeMethods
        {
            public const int NO_ERROR = 0;
            public const int ERROR_SHARING_VIOLATION = 32;
            public const uint FILE_GENERIC_READ = 0x00120089;

            [DllImport("kernel32.dll", SetLastError = true)]
            [SuppressUnmanagedCodeSecurity]
            public static extern SafeFileHandle ReOpenFile(SafeFileHandle hFile, uint access, FileShare mode, uint flags);
        }
    }
}
