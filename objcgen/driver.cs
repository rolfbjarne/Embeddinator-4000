﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using IKVM.Reflection;
using Type = IKVM.Reflection.Type;
using System.Text;

using ObjC;

namespace Embeddinator {
	
	static class Driver {

		// TODO: use Mono.Options

		static int Main (string [] args)
		{
			try {
				return Main2 (args);
			} catch (Exception e) {
				ErrorHelper.Show (e);
				return 1;
			}
		}

		public static int Main2 (string [] args)
		{
			bool shared = true; // dylib

			Console.WriteLine ("Parsing assemblies...");

			var universe = new Universe (UniverseOptions.MetadataOnly);
			var assemblies = new List<Assembly> ();
			foreach (var arg in args) {
				assemblies.Add (universe.LoadFile (arg));
				Console.WriteLine ($"\tParsed '{arg}'");
			}

			// by default the first specified assembly
			var name = Path.GetFileNameWithoutExtension (args [0]);

			Console.WriteLine ("Processing assemblies...");
			var g = new ObjCGenerator ();
			g.Process (assemblies);

			Console.WriteLine ("Generating binding code...");
			g.Generate (assemblies);
			g.Write ();

			var exe = typeof (Driver).Assembly;
			foreach (var res in exe.GetManifestResourceNames ()) {
				if (res == "main.c") {
					// no main is needed for dylib and don't re-write an existing main.c file - it's a template
					if (shared || File.Exists ("main.c"))
						continue; 
				}
				Console.WriteLine ($"\tGenerated: {res}");
				using (var sw = new StreamWriter (res))
					exe.GetManifestResourceStream (res).CopyTo (sw.BaseStream);
			}

			Console.WriteLine ("Compiling binding code...");

			StringBuilder options = new StringBuilder ("clang ");
			options.Append ("-DMONO_EMBEDDINATOR_DLL_EXPORT ");
			options.Append ("-framework CoreFoundation ");
			options.Append ("-I\"/Library/Frameworks/Mono.framework/Versions/Current/include/mono-2.0\" -L\"/Library/Frameworks/Mono.framework/Versions/Current/lib/\" -lmonosgen-2.0 ");
			options.Append ("glib.c mono_embeddinator.c bindings.m ");
			if (shared)
				options.Append ($"-dynamiclib -install_name lib{name}.dylib ");
			else
				options.Append ("main.c ");
			options.Append ($"-o lib{name}.dylib -ObjC -lobjc");

			Console.WriteLine ("Compiling binding code...");
			Console.WriteLine ($"\tInvoking: xcrun {options}");
			var p = Process.Start ("xcrun", options.ToString ());
			p.WaitForExit ();
			Console.WriteLine ("Done");
			return p.ExitCode;
		}
	}
}
