using Fleck;
using System.Text.Json;

namespace CalibrationEnv
{
    public enum WorldUpdateSource
    {
        Resonite,
        Client
    }

    internal class Adaptor
    {
        // reference to world model to obtain world data 
        protected WorldModel worldModel;

        // reference to connected socket
        protected IWebSocketConnection? socket;

        // interval times for requesting slots, in ms
        protected int rootMsgInterval = 1000;
        protected int childMsgInterval = 17;

        public Adaptor(WorldModel worldModel, IWebSocketConnection? socket)
        {
            this.worldModel = worldModel;
            this.socket = socket;
        }

        /// <summary>
        /// Called when receiving msg from client. 
        /// Processes message and update world model accordingly.
        /// </summary>
        /// <param name="msgRoot">JSONElement containing the root of the message.</param>
        public virtual void Receive(JsonElement msgRoot)
        {
            worldModel.ApplyUpdate(WorldUpdateSource.Client, msgRoot);
            Console.WriteLine($"Client {socket?.ConnectionInfo.Id} send msg, succesfully received: {msgRoot.ToString()}");
        }

        /// <summary>
        /// Called when client wants to receive a message,
        /// containing current world model data and 
        /// is send to the client matching the socket.
        /// </summary>
        protected virtual async Task Send()
        {
            while (true)
            {
                try
                {
                    if (socket != null && socket.IsAvailable)
                    {
                        await socket.Send(worldModel.GetWorldModelJson()?.ToString());
                    }

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending message to client, scheduling removal: {ex.Message}");
                    // TODO: somehow remove from session manager adaptors and clients collections,
                    // to avoid trying to send to this socket again
                }

                await Task.Delay(childMsgInterval);
            }
        }
    }
}