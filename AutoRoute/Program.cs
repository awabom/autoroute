using RouteLibrary;
using System;

namespace AutoRoute
{
	class Program
    {
        static void Main(string[] args)
        {
			if (args.Length != 1)
			{
				Console.Error.WriteLine("Usage: AutoRoute <path to OpenCPN navobj.xml>");
				Environment.Exit(1);
			}
			else
			{
				AutoRouter autoRouter = new AutoRouter();
				autoRouter.MakeRoutes(args[0]);
				Environment.Exit(0);
			}
        }
    }
}
