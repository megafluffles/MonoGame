using System;
using System.Diagnostics;
#if DEBUG
using System.Runtime.Serialization;
#endif

namespace Microsoft.Xna.Framework.Net
{
#if WINDOWS_UAP
    [DataContract]
#else
    [Serializable]
#endif
    public class NetworkSessionJoinException : NetworkException
    {
        public NetworkSessionJoinException()
        {
#if DEBUG
            Debugger.Break();
#endif
        }

        public NetworkSessionJoinException(string message) : base(message)
        {
#if DEBUG
            Debugger.Break();
#endif
        }

        public NetworkSessionJoinException(string message, Exception innerException) : base(message, innerException)
        {
#if DEBUG
            Debugger.Break();
#endif
        }

        public NetworkSessionJoinException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
#if DEBUG
            Debugger.Break();
#endif
        }

        public NetworkSessionJoinException(string message, NetworkSessionJoinError joinError) : base(message)
        {
            this.JoinError = joinError;
        }

        public NetworkSessionJoinError JoinError { get; set; }
    }
}
