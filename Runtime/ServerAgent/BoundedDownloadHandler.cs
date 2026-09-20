using System.IO;
using System.Text;
using UnityEngine.Networking;

namespace PlayFlow.Nakama.Fleet.Server
{
    // A character check after DownloadHandlerBuffer finishes would not bound network memory usage.
    internal sealed class BoundedDownloadHandler : DownloadHandlerScript
    {
        private readonly MemoryStream _buffer = new MemoryStream();
        private readonly int _maximumBytes;
        public bool LimitExceeded { get; private set; }

        public BoundedDownloadHandler(int maximumBytes) : base(new byte[8192])
        {
            _maximumBytes = maximumBytes;
        }

        protected override void ReceiveContentLengthHeader(ulong contentLength)
        {
            if (contentLength > (ulong)_maximumBytes) LimitExceeded = true;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength < 0 || LimitExceeded || _buffer.Length + dataLength > _maximumBytes)
            {
                LimitExceeded = true;
                return false;
            }
            _buffer.Write(data, 0, dataLength);
            return true;
        }

        public string Body => new UTF8Encoding(false, true).GetString(_buffer.ToArray());

        public override void Dispose()
        {
            _buffer.Dispose();
            base.Dispose();
        }
    }
}
