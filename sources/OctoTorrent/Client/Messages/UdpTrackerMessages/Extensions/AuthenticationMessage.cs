namespace OctoTorrent.Client.Messages.UdpTracker
{
    using System;
    using System.Text;

    internal class AuthenticationMessage : Message
    {
        private byte[] password;
        private string username;
        private byte usernameLength;

        public override int ByteLength
        {
            get { return 4 + usernameLength + 8; }
        }

        public override void Decode(byte[] buffer, int offset, int length)
        {
            usernameLength = buffer[offset];
            offset++;
            username = Encoding.ASCII.GetString(buffer, offset, usernameLength);
            offset += usernameLength;
            password = new byte[8];
            Buffer.BlockCopy(buffer, offset, password, 0, password.Length);
        }

        public override int Encode(byte[] buffer, int offset)
        {
            var written = Write(buffer, offset, usernameLength);
            var name = Encoding.ASCII.GetBytes(username);
            written += Write(buffer, offset, name, 0, name.Length);
            written += Write(buffer, offset, password, 0, password.Length);

            CheckWritten(written);
            return written;
        }
    }
}