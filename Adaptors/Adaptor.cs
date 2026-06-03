using System.Diagnostics;
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

        // reference to tasks running 
        protected readonly List<Task> tasks = [];

        // unique identifier for this adaptor,
        // generated on connect and used to identify client 
        public string Id { get; protected set; }      

        // default send rate of ~60fps, can be set in constructor
        protected readonly int sendIntervalMs = 16; 

        // state of send socket
        protected abstract bool IsSendReady { get; }

        /// <summary>
        /// Constructor for Adaptor
        /// </summary>
        /// <param name="worldModel">The world model to be used by the adaptor.</param>
        public Adaptor(WorldModel worldModel, int sendIntervalMs)
        {
            this.worldModel = worldModel;
            
            Id = "";
        }

        /// <summary>
        /// Starts async loop to send messages to client on interval. 
        /// </summary>
        /// <param name="cts"></param>
        /// <returns></returns>
        public virtual Task StartAsync(CancellationToken token)
        {
            // start send loop
            tasks.Add(RunSendLoop(token));

            // return success
            return Task.CompletedTask;
        }
        
        public async Task EndAsync()
        {
            await Task.WhenAll(tasks);
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
                // get start time for this function
                // to account for time spent here when waiting for next interval
                var start = Stopwatch.GetTimestamp();

                try
                {
                    await Send(token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"SendLoop error: {ex.Message} {ex.StackTrace}");
                }

                // wait remaining time in interval, accounting for time spent processing
                await DelayRemainingAsync(start, sendIntervalMs, token);
            }
        }

        protected abstract Task Send(CancellationToken token);

        /// <summary>
        /// Called when receiving msg from client. 
        /// Processes message and update world model accordingly.
        /// </summary>
        /// <param name="msgRoot">JSONElement containing the root of the message.</param>
        public abstract void Receive(JsonElement msgRoot);

        /// <summary>
        /// Helper function to delay for the remaining time in the send interval, 
        /// accounting for time already spent processing.
        /// </summary>
        /// <param name="startTimestamp">Start timestamp</param>
        /// <param name="intervalMs">Interval in milliseconds</param>
        /// <param name="token">Cancellation token</param>
        /// <returns></returns>
        protected static async Task DelayRemainingAsync(long startTimestamp, int intervalMs, CancellationToken token)
        {
            // calculate time elapsed since start
            // use of stopwatch ticks allows for more accurate and stable timing 
            // gives ticks which are calculated to ms based on frequency
            var elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

            // wait remaining time in interval, if any
            await Task.Delay(Math.Max(intervalMs - (int)elapsedMs, 0), token);
        }

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

            Console.WriteLine($"Generated ID: {prefix}-{new string(result)}");

			return $"{prefix}-{new string(result)}";
		}
	}
}