using System;
using Google.Protobuf;

namespace Scripts.VecEnv.Message
{
    public static class ByteStringUtils
    {
        public static ByteString FloatArrayToByteString(float[] values)
        {
            if (values == null || values.Length == 0)
            {
                return ByteString.Empty;
            }

            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return ByteArrayToByteString(bytes);
        }

        public static ByteString IntArrayToByteString(int[] values)
        {
            if (values == null || values.Length == 0)
            {
                return ByteString.Empty;
            }

            var bytes = new byte[values.Length * sizeof(int)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return ByteArrayToByteString(bytes);
        }

        public static ByteString StateArrayToByteString(EnvironmentState[] states, EnvironmentState activeState)
        {
            if (states == null || states.Length == 0)
            {
                return ByteString.Empty;
            }

            var bytes = new byte[states.Length];
            for (int i = 0; i < states.Length; i++)
            {
                bytes[i] = states[i] == activeState ? (byte)1 : (byte)0;
            }

            return ByteArrayToByteString(bytes);
        }

        public static ByteString ByteArrayToByteString(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return ByteString.Empty;
            }

            return UnsafeByteOperations.UnsafeWrap(new ReadOnlyMemory<byte>(bytes));
        }
    }
}
