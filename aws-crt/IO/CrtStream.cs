using System;
using System.IO;
using System.Runtime.InteropServices;

using Aws.Crt;

namespace Aws.Crt.IO
{
    public sealed class CrtStreamWrapper
    {
        public enum StreamState 
        {
            InProgress = 0,
            Done = 1,
        }

        /* Match native aws_stream_seek_basis */
        public enum SeekBasis {
            Begin = 0,
            End = 2
        }

        public delegate int CrtStreamReadCallback(
                        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex=1)] byte[] buffer, 
                        UInt64 size,
                        out UInt64 bytesWritten);
        public delegate bool CrtStreamSeekCallback(Int64 offset, int basis);

        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
        public struct DelegateTable
        {
            public CrtStreamReadCallback ReadCallback;
            public CrtStreamSeekCallback SeekCallback;
        }

        private Stream BodyStream;

        public DelegateTable Delegates { get; private set; }

        private SeekOrigin SeekBasisToSeekOrigin(SeekBasis basis) {
            switch(basis) {
                case SeekBasis.Begin:
                    return SeekOrigin.Begin;

                case SeekBasis.End:
                    return SeekOrigin.End;
            }

            throw new ArgumentException("Seek basis must be Begin or End");
        }

        private bool SeekInternal(long offset, int basis) {
            SeekBasis realBasis = (SeekBasis) basis;

            try {
                if (BodyStream.CanSeek) {
                    BodyStream.Seek(offset, SeekBasisToSeekOrigin(realBasis));
                    return true;
                }
            } catch (ArgumentException) {
                ;
            }

            return false;
        }

        private int ReadInternal(byte[] buffer, ulong size, out ulong bytesWritten) {
            bytesWritten = 0;
            
            Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: CrtStreamWrapper.ReadInternal called");
            Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: Buffer size: {buffer?.Length ?? 0}, Requested: {size}");
            
            if (BodyStream != null && BodyStream.CanRead)
            {
                Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: BodyStream position: {BodyStream.Position}, length: {BodyStream.Length}");
                
                // CRITICAL FIX: Read FROM BodyStream INTO buffer (not the other way around!)
                int maxBytesToRead = (int)Math.Min((long)size, (long)buffer.Length);  
                int availableBytes = (int)Math.Min((long)maxBytesToRead, BodyStream.Length - BodyStream.Position);

                
                Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: Max bytes to read: {maxBytesToRead}, Available: {availableBytes}");
                
                if (availableBytes > 0)
                {
                    int bytesRead = BodyStream.Read(buffer, 0, availableBytes);
                    bytesWritten = (ulong)bytesRead;
                    
                    Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: Bytes actually read: {bytesRead}");
                    Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: New BodyStream position: {BodyStream.Position}");
                    
                    // Return InProgress if more data available, Done if we've reached the end
                    if (BodyStream.Position >= BodyStream.Length)
                    {
                        Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: *** END OF STREAM REACHED - Returning DONE ***");
                        return (int)StreamState.Done;
                    }
                    else
                    {
                        Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: More data available - Returning IN_PROGRESS");
                        return (int)StreamState.InProgress;
                    }
                }
                else
                {
                    Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: No bytes available - Returning DONE");
                    return (int)StreamState.Done;
                }
            }
            
            Console.WriteLine($"DEADLOCK-TRACE-DOTNET-READ: BodyStream is null or unreadable - Returning DONE");
            return (int)StreamState.Done;
        }

        public CrtStreamWrapper(Stream stream)
        {
            BodyStream = stream;
            var delegates = new DelegateTable();

            /*
             * We pass the delegate table by value to C, so we indicate a null stream with a nulled table.
             */
            if (stream != null) {
                delegates.ReadCallback = ReadInternal;
                delegates.SeekCallback = SeekInternal;
            } else {
                delegates.ReadCallback = null;
                delegates.SeekCallback = null;
            }

            Delegates = delegates;
        }
    }
}
