using Fleck;
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

        public Adaptor(WorldModel worldModel)
        {
            this.worldModel = worldModel;
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
                    Console.WriteLine($"SendLoop error: {ex.Message}");
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
    }
}