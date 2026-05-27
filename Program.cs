namespace CalibrationEnv
{
    public class Program
    {
        public const bool WITH_RESONITE = true;

        public static async Task Main()
        {
            // setup session manager
            // will handle adaptor connections and world updates 
            var sessionManager = new SessionManager(WITH_RESONITE);

            // setup central cancellation token source
            // to allow graceful shutdown on Ctrl+C
            var cts = new CancellationTokenSource();

            // handle Ctrl+C to trigger cancellation
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // run session manager until cancellation is requested
            await sessionManager.RunAsync(cts.Token);
        }
    }
}