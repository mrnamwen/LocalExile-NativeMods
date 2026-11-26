namespace LocalExile.Client.Cli;

public static class Program
{
    public static int Main(string[] args) =>
        NativeModsCliApp.Run(args, Console.Out, Console.Error);
}
