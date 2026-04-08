namespace CalibrationEnv
{
    public class Program
    {
        public static async Task Main()
        {
            var sessionManager = new SessionManager();

            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await sessionManager.RunAsync(cts.Token);
        }
    }
}