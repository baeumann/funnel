// See https://aka.ms/new-console-template for more information

using System.Net;
using funnel.core;

//minecraft port
const int PORT = 25565;
const string bootLogo = "    ______   __  __    _   __    _   __    ______    __ \n   / ____/  / / / /   / | / /   / | / /   / ____/   / / \n  / /_     / / / /   /  |/ /   /  |/ /   / __/     / /  \n / __/    / /_/ /   / /|  /   / /|  /   / /___    / /___   _______\n/_/       \\____/   /_/ |_/   /_/ |_/   /_____/   /_____/  /______/\n                                                        ";

Console.WriteLine("\n\n");
Console.Write(bootLogo);
Console.WriteLine("\n____________________________________________________________________________");
Console.WriteLine("\nWELCOME TO FUNNEL MINECRAFT PROXY [Press any key to exit]\nRESTART IF LOCAL SERVER SCAN IS ALREADY CONNECTED\n");

Proxy proxy;
if(args.Length >= 2) {
    bool.TryParse(args[1], out Proxy.LOCAL_DEBUG);

    if(Proxy.LOCAL_DEBUG) {
        Console.WriteLine("DEBUG MODE, USING SERVER PORT " + Proxy.DEBUG_PORT);
    }
}
if(args.Length < 1) {
    Console.WriteLine("TARGET [localhost]\n");
    proxy = new Proxy(PORT, "127.0.0.1");
} else {
    Console.WriteLine("TARGET [" + args[0] + "]\n");
    proxy = new Proxy(PORT, args[0]);
}

Console.ReadLine();
Console.WriteLine("PROXY EXITING!");
proxy.Close();
Console.WriteLine("BYE!");
