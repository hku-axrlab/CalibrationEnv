namespace CalibrationEnv
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // determine whether to connect with Resonite
            // based on command-line arguments
            var withResonite = HasArgument(args, "--with-resonite");

            // setup session manager
            // will handle adaptor connections and world updates 
            var sessionManager = new SessionManager(withResonite);

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

        private static bool HasArgument(string[] args, string argument)
        {
            return args.Any(a => string.Equals(a, argument, StringComparison.OrdinalIgnoreCase));
        }
    }
}