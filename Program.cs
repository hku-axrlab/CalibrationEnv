using Fleck;
using ResoniteLink;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CalibrationEnv
{
    public class Program
    {
        private static SessionManager sessionManager;
        private static Task sessionTask;

        public static async Task Main()
        {
            sessionManager = new SessionManager();
            //sessionTask = sessionManager.Main();

            await sessionManager.Main();

            // idk what else go figure 
        }





    }
}