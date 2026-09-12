namespace AutostartAudit.Cli;

public static class Program
{
    public static int Main(string[] args) =>
        App.Run(args, Console.Out, Console.Error);
}
