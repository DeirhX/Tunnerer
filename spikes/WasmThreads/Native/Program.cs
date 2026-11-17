int ticks = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 600;
Spike.Bench.RunAll(Console.WriteLine, [1, 2, 4, 8], ticks);
