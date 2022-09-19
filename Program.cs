// See https://aka.ms/new-console-template for more information

using System.Net;
using funnel.core;

//minecraft port
const int PORT = 25565;
const string bootLogo = "    ______   __  __    _   __    _   __    ______    __ \n   / ____/  / / / /   / | / /   / | / /   / ____/   / / \n  / /_     / / / /   /  |/ /   /  |/ /   / __/     / /  \n / __/    / /_/ /   / /|  /   / /|  /   / /___    / /___   _______\n/_/       \\____/   /_/ |_/   /_/ |_/   /_____/   /_____/  /______/\n                                                        ";

Console.WriteLine("\n\n");
Console.Write(bootLogo);
Console.WriteLine("\n____________________________________________________________________________");
Console.WriteLine("\nWELCOME TO FUNNEL MINECRAFT PROXY [Press any key to exit]\n\n\n");

if(args.Length < 1) {
    Console.WriteLine(">> please enter an address for target server connection");

    Console.WriteLine("PROXY EXITING!");
    Console.WriteLine("BYE!");
} else {
    var proxy = new Proxy(PORT, args[0]);

    Console.ReadLine();
    Console.WriteLine("PROXY EXITING!");
    proxy.Close();
    Console.WriteLine("BYE!");
}
