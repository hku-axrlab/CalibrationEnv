using System.Net;
using System.Text.Json;

namespace CalibrationEnv
{
    public enum WorldUpdateSource
    {
        Resonite,
        Client
    }

    internal abstract class Adaptor
    {
        // reference to world model to obtain world data 
        protected readonly WorldModel worldModel;
        protected string guid;
        public string GUID => guid;

        public Adaptor(WorldModel worldModel)
        {
            this.worldModel = worldModel;
            this.guid = "";
        }

        /// <summary>
        /// Starts async loop to send messages to client on interval. 
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public virtual Task StartAsync(CancellationToken token)
        {
            Task.Run(() => RunSendLoop(token), token);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs async on interval client wants to receive messages.
        /// Messages contain current world model data and 
        /// are sent to the client matching the socket.
        /// </summary>
        private async Task RunSendLoop(CancellationToken token) 
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await SendStep();
                }
                catch (Exception ex)
                {
                    //Console.WriteLine($"SendLoop error: {ex.Message}");
                }

                await Task.Delay(GetSendInterval(), token);
            }
        }

        protected abstract Task SendStep();
        protected abstract int GetSendInterval();

        /// <summary>
        /// Called when receiving msg from client. 
        /// Processes message and update world model accordingly.
        /// </summary>
        /// <param name="msgRoot">JSONElement containing the root of the message.</param>
        public abstract void Receive(JsonElement msgRoot);

		/// <summary>
		/// Function to generate a unique string-id for an Adaptor.
		/// </summary>
		/// <param name="adaptorType"></param>
		/// <param name="ipString">Must be IPv4</param>
		/// <param name="port"></param>
		/// <returns></returns>
		public static string GenerateId(string adaptorType, string ipString, uint port)
		{
            IPAddress ip = IPAddress.Parse(ipString);

			// Combine IP bytes + port into a single uint to hash
			byte[] ipBytes = ip.GetAddressBytes(); // assumes IPv4
			uint seed = BitConverter.ToUInt32(ipBytes, 0);

			// FNV-32a hash
			uint hash = 2166136261u;
			for (int i = 0; i < 4; i++)
			{
				hash ^= (byte)(seed >> (i * 8));
				hash *= 16777619u;
			}

			// Encode as base-36, 6 chars
			const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
			char[] result = new char[6];
			for (int i = 5; i >= 0; i--)
			{
				result[i] = chars[(int)(hash % 36)];
				hash /= 36;
			}

            string typeStr = adaptorType;

			string prefix = typeStr.Length >= 3
				? typeStr[..3].ToUpper()
				: typeStr.ToUpper().PadRight(3, 'X');

            Console.WriteLine($"Generated GUID: {prefix}-{new string(result)}");

			return $"{prefix}-{new string(result)}";
		}
	}
}