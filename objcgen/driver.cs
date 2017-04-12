using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using IKVM.Reflection;
using Type = IKVM.Reflection.Type;
using System.Text;

using Mono.Options;

using ObjC;

namespace Embeddinator {

	enum Action
	{
		None,
		Help,
		Version,
		Generate,
	}

	public enum Platform
	{
		macOS,
		iOS,
		watchOS,
		tvOS,
	}

	public enum CompilationTarget
	{
		SharedLibrary,
		StaticLibrary,
		Framework,
	}

	public enum TargetLanguage
	{
		ObjectiveC,
	}

	public static class Driver
	{
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
			var action = Action.None;

			var os = new OptionSet {
				{ "c|compile", "Compiles the generated output", v => CompileCode = true },
				{ "d|debug", "Build the native library with debug information.", v => Debug = true },
				{ "gen=", $"Target generator (default {TargetLanguage})", v => SetTarget (v) },
				{ "o|out|outdir=", "Output directory", v => OutputDirectory = v },
				{ "p|platform=", $"Target platform (iOS, macOS [default], watchOS, tvOS)", v => SetPlatform (v) },
				{ "dll|shared", "Compiles as a shared library (default)", v => CompilationTarget = CompilationTarget.SharedLibrary },
				{ "static", "Compiles as a static library (unsupported)", v => CompilationTarget = CompilationTarget.StaticLibrary },
				{ "vs=", $"Visual Studio version for compilation (unsupported)", v => { throw new EmbeddinatorException (2, $"Option `--vs` is not supported"); } },
				{ "h|?|help", "Displays the help", v => action = Action.Help },
				{ "v|verbose", "generates diagnostic verbose output", v => ErrorHelper.Verbosity++ },
				{ "version", "Display the version information.", v => action = Action.Version },
				{ "target=", "The compilation target (static, shared, framework).", v => SetCompilationTarget (v) },
			};

			var assemblies = os.Parse (args);

			if (action == Action.None && assemblies.Count > 0)
				action = Action.Generate;

			switch (action) {
			case Action.None:
			case Action.Help:
				Console.WriteLine ($"Embeddinator-4000 v0.1 ({Info.Branch}: {Info.Hash})");
				Console.WriteLine ("Generates target language bindings for interop with managed code.");
				Console.WriteLine ("");
				Console.WriteLine ($"Usage: {Path.GetFileName (System.Reflection.Assembly.GetExecutingAssembly ().Location)} [options]+ ManagedAssembly1.dll [ManagedAssembly2.dll ...]");
				Console.WriteLine ();
				os.WriteOptionDescriptions (Console.Out);
				return 0;
			case Action.Version:
				Console.WriteLine ($"Embeddinator-4000 v0.1 ({Info.Branch}: {Info.Hash})");
				return 0;
			case Action.Generate:
				try {
					var result = Generate (assemblies);
					if (CompileCode && (result == 0))
						result = Compile ();
					Console.WriteLine ("Done");
					return result;
				} catch (NotImplementedException e) {
					throw new EmbeddinatorException (1000, $"The feature `{e.Message}` is not currently supported by the tool");
				}
			default:
				throw ErrorHelper.CreateError (99, "Internal error: invalid action {0}. Please file a bug report with a test case (https://github.com/mono/Embeddinator-4000/issues)", action);
			}
		}

		static string outputDirectory = ".";

		public static string OutputDirectory {
			get { return outputDirectory; }
			set {
				if (!Directory.Exists (value)) {
					try {
						Directory.CreateDirectory (value);
						Console.WriteLine ($"Creating output directory `{Path.GetFullPath (value)}`");
					} catch (Exception e) {
						throw new EmbeddinatorException (1, true, e, $"Could not create output directory `{value}`");
					}
				}
				outputDirectory = value;
			}
		}

		static bool CompileCode { get; set; }

		static bool Debug { get; set; }

		static bool Shared { get { return CompilationTarget == CompilationTarget.SharedLibrary; } }

		static string LibraryName { get; set; }

		public static void SetPlatform (string platform)
		{
			switch (platform.ToLowerInvariant ()) {
			case "osx":
			case "macosx":
			case "macos":
			case "mac":
				Platform = Platform.macOS;
				break;
			case "ios":
				Platform = Platform.iOS;
				break;
			case "tvos":
				Platform = Platform.tvOS;
				break;
			case "watchos":
				Platform = Platform.watchOS;
				break;
			default:
				throw new EmbeddinatorException (3, true, $"The platform `{platform}` is not valid.");
			}
		}

		public static void SetTarget (string value)
		{
			switch (value.ToLowerInvariant ()) {
			case "objc":
			case "obj-c":
			case "objectivec":
			case "objective-c":
				TargetLanguage = TargetLanguage.ObjectiveC;
				break;
			default:
				throw new EmbeddinatorException (4, true, $"The target `{value}` is not valid.");
			}
		}

		public static void SetCompilationTarget (string value)
		{
			switch (value.ToLowerInvariant ()) {
			case "library":
			case "sharedlibrary":
			case "dylib":
				CompilationTarget = CompilationTarget.SharedLibrary;
				break;
			case "framework":
				CompilationTarget = CompilationTarget.Framework;
				break;
			case "static":
			case "staticlibrary":
				CompilationTarget = CompilationTarget.StaticLibrary;
				break;
			default:
				throw new EmbeddinatorException (5, true, $"The compilation target `{value}` is not valid.");
			}
		}
		public static List<Assembly> Assemblies { get; private set; } = new List<Assembly> (); 
		public static Platform Platform { get; set; } = Platform.macOS;
		public static TargetLanguage TargetLanguage { get; private set; } = TargetLanguage.ObjectiveC;
		public static CompilationTarget CompilationTarget { get; set; } = CompilationTarget.SharedLibrary;

		static int Generate (List<string> args)
		{
			Console.WriteLine ("Parsing assemblies...");

			var universe = new Universe (UniverseOptions.MetadataOnly);
			foreach (var arg in args) {
				Assemblies.Add (universe.LoadFile (arg));
				Console.WriteLine ($"\tParsed '{arg}'");
			}

			// if not specified then we use the first specified assembly name
			if (LibraryName == null)
				LibraryName = Path.GetFileNameWithoutExtension (args [0]);

			Console.WriteLine ("Processing assemblies...");
			var g = new ObjCGenerator ();
			g.Process (Assemblies);

			Console.WriteLine ("Generating binding code...");
			g.Generate (Assemblies);
			g.Write (OutputDirectory);

			var exe = typeof (Driver).Assembly;
			foreach (var res in exe.GetManifestResourceNames ()) {
				if (res == "main.c") {
					// no main is needed for dylib and don't re-write an existing main.c file - it's a template
					if (CompilationTarget != CompilationTarget.StaticLibrary || File.Exists ("main.c"))
						continue;
				}
				var path = Path.Combine (OutputDirectory, res);
				Console.WriteLine ($"\tGenerated: {path}");
				using (var sw = new StreamWriter (path))
					exe.GetManifestResourceStream (res).CopyTo (sw.BaseStream);
			}
			return 0;
		}

		static string xcode_app;
		static string XcodeApp {
			get {
				if (string.IsNullOrEmpty (xcode_app)) {
					int exitCode;
					string output;
					if (!RunProcess ("xcode-select", "-p", out exitCode, out output))
						throw ErrorHelper.CreateError (6, "Could not find the Xcode location.");
					xcode_app = Path.GetDirectoryName (Path.GetDirectoryName (output.Trim ()));
				}
				return xcode_app;
			}
		}

		class BuildInfo
		{
			public string Sdk;
			public string [] Architectures;
			public string SdkName; // used in -m{SdkName}-version-min
			public string MinVersion;
			public string XamariniOSSDK;
			public string CompilerFlags;
			public string LinkerFlags;

			public bool IsSimulator {
				get { return Sdk.Contains ("Simulator"); }
			}
		}

		static int Compile ()
		{
			Console.WriteLine ("Compiling binding code...");

			BuildInfo [] build_infos;

			switch (Platform) {
			case Platform.macOS:
				build_infos = new BuildInfo [] {
					new BuildInfo { Sdk = "MacOSX", Architectures = new string [] { "i386", "x86_64" }, SdkName = "macosx", MinVersion = "10.7" },
				};
				break;
			case Platform.iOS:
				build_infos = new BuildInfo [] {
					new BuildInfo { Sdk = "iPhoneOS", Architectures = new string [] { "armv7", "armv7s", "arm64" }, SdkName = "iphoneos", MinVersion = "8.0", XamariniOSSDK = "MonoTouch.iphoneos.sdk" },
					new BuildInfo { Sdk = "iPhoneSimulator", Architectures = new string [] { "i386", "x86_64" }, SdkName = "ios-simulator", MinVersion = "8.0", XamariniOSSDK = "MonoTouch.iphonesimulator.sdk" },
				};
				break;
			case Platform.tvOS:
				build_infos = new BuildInfo [] {
					new BuildInfo { Sdk = "AppleTVOS", Architectures = new string [] { "arm64" }, SdkName = "tvos", MinVersion = "9.0", XamariniOSSDK = "Xamarin.AppleTVOS.sdk", CompilerFlags = "-fembed-bitcode", LinkerFlags = "-fembed-bitcode" },
					new BuildInfo { Sdk = "AppleTVSimulator", Architectures = new string [] { "x86_64" }, SdkName = "tvos-simulator", MinVersion = "9.0", XamariniOSSDK = "Xamarin.AppleTVSimulator.sdk" },
				};
				break;
			case Platform.watchOS:
				build_infos = new BuildInfo [] {
					new BuildInfo { Sdk = "WatchOS", Architectures = new string [] { "armv7k" }, SdkName = "watchos", MinVersion = "2.0", XamariniOSSDK = "Xamarin.WatchOS.sdk", CompilerFlags = "-fembed-bitcode", LinkerFlags = "-fembed-bitcode"  },
					new BuildInfo { Sdk = "WatchSimulator", Architectures = new string [] { "i386" }, SdkName = "watchos-simulator", MinVersion = "2.0", XamariniOSSDK = "Xamarin.WatchSimulator.sdk" },
				};
				break;
			default:
				throw ErrorHelper.CreateError (99, "Internal error: invalid platform {0}. Please file a bug report with a test case (https://github.com/mono/Embeddinator-4000/issues).", Platform);
			}

			var lipo_files = new List<string> ();
			var output_file = string.Empty;

			var files = new string [] {
				Path.Combine (OutputDirectory, "glib.c"),
				Path.Combine (OutputDirectory, "mono_embeddinator.c"),
				Path.Combine (OutputDirectory, "objc-support.m"),
				Path.Combine (OutputDirectory, "bindings.m"),
			};

			switch (CompilationTarget) {
			case CompilationTarget.SharedLibrary:
				output_file = $"lib{LibraryName}.dylib";
				break;
			case CompilationTarget.StaticLibrary:
			case CompilationTarget.Framework:
				output_file = $"{LibraryName}.a";
				break;
			default:
				throw ErrorHelper.CreateError (99, "Internal error: invalid compilation target {0}. Please file a bug report with a test case (https://github.com/mono/Embeddinator-4000/issues).", CompilationTarget);
			}

			int exitCode;

			/*
			 * For static libraries we
			 * * Compile all source files to .o files, per architecture.
			 * * Then we archive .o files into per-sdk .a files, then we archive all .o files into a global .a file. So we end up with one .a for device, one for simulator, and one for both device and simulator.
			 * * Then we dsym the global .a file
			 * 
			 * For dynamic libraries we
			 * * Compile all source files to .o files, per architecture.
			 * * Then we link all .o files per architecture into a .dylib.
			 * * Then we lipo .dylib files into per-sdk fat .dylib, then we lipo all .dylbi into a global .dylib file. So we end up with one fat .dylib for device, one fat .dylib for simulator, and one very fat .dylib for both simulator and device.
			 * * Finally we dsymutil the global .dylib
			 * 
			 * For frameworks we
			 * * First we build the source files to .o files and then archive to .a files just like for static libraries.
			 * * Then we call mtouch to build a framework of the managed assemblies, passing the static library we just created as a gcc_flag so that it's linked in. This is done per sdk (simulator/device).
			 * * We don't create a global framework, we stop at the per-sdk frameworks mtouch created.
			 * * We dsymutil those frameworks.
			 */

			foreach (var build_info in build_infos) {
				foreach (var arch in build_info.Architectures) {
					var archOutputDirectory = Path.Combine (OutputDirectory, arch);
					Directory.CreateDirectory (archOutputDirectory);

					var common_options = new StringBuilder ("clang ");
					if (Debug)
						common_options.Append ("-g -O0 ");
					else
						common_options.Append ("-O2 -DTOKENLOOKUP ");
					common_options.Append ("-fobjc-arc ");
					common_options.Append ("-ObjC ");
					common_options.Append ("-Wall ");
					common_options.Append ($"-arch {arch} ");
					common_options.Append ($"-isysroot {XcodeApp}/Contents/Developer/Platforms/{build_info.Sdk}.platform/Developer/SDKs/{build_info.Sdk}.sdk ");
					common_options.Append ($"-m{build_info.SdkName}-version-min={build_info.MinVersion} ");

					switch (Platform) {
					case Platform.macOS:
						// FIXME: Pending XM support to switch include directory.
						//common_options.Append ("-I/Library/Frameworks/Xamarin.Mac.framework/Versions/Current/include ");
						//common_options.Append ("-DXAMARIN_MAC ");
						common_options.Append ("-I/Library/Frameworks/Mono.framework/Versions/Current/include/mono-2.0 ");
						break;
					case Platform.iOS:
					case Platform.tvOS:
					case Platform.watchOS:
						common_options.Append ($"-I/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/SDKs/{build_info.XamariniOSSDK}/usr/include ");
						common_options.Append ("-DXAMARIN_IOS ");
						break;
					default:
						throw ErrorHelper.CreateError (99, "Internal error: invalid platform {0}. Please file a bug report with a test case (https://github.com/mono/Embeddinator-4000/issues).", Platform);
					}

					// Build each source file to a .o
					var object_files = new List<string> ();
					foreach (var file in files) {
						var compiler_options = new StringBuilder (common_options.ToString ());
						compiler_options.Append ("-DMONO_EMBEDDINATOR_DLL_EXPORT ");
						compiler_options.Append (build_info.CompilerFlags).Append (" ");
						compiler_options.Append ("-c ");
						compiler_options.Append (Quote (file)).Append (" ");
						var objfile = Path.Combine (archOutputDirectory, Path.ChangeExtension (Path.GetFileName (file), "o"));
						compiler_options.Append ($"-o {Quote (objfile)} ");
						object_files.Add (objfile);
						if (!Xcrun (compiler_options, out exitCode))
							return exitCode;
					}

					// Link/archive object files to .a/.dylib
					switch (CompilationTarget) {
					case CompilationTarget.SharedLibrary:
						// Link all the .o files into a .dylib
						var options = new StringBuilder (common_options.ToString ());
						options.Append ($"-dynamiclib ");
						options.Append (build_info.LinkerFlags).Append (" ");
						options.Append ("-lobjc ");
						options.Append ("-framework CoreFoundation ");
						options.Append ("-framework Foundation ");
						options.Append ($"-install_name {Quote ("@rpath/" + output_file)} ");

						foreach (var objfile in object_files)
							options.Append (Quote (objfile)).Append (" ");

						var dynamic_ofile = Path.Combine (archOutputDirectory, output_file);
						options.Append ($"-o ").Append (Quote (dynamic_ofile)).Append (" ");
						lipo_files.Add (dynamic_ofile);
						if (!string.IsNullOrEmpty (build_info.XamariniOSSDK)) {
							options.Append ($"-L/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/SDKs/{build_info.XamariniOSSDK}/usr/lib ");
							options.Append ("-lxamarin ");
						} else {
							options.Append ("-L/Library/Frameworks/Mono.framework/Versions/Current/lib/ ");
						}
						options.Append ("-lmonosgen-2.0 ");
						if (!Xcrun (options, out exitCode))
							return exitCode;
						break;
					case CompilationTarget.Framework:
					case CompilationTarget.StaticLibrary:
						// Archive all the .o files into a .a
						var archive_options = new StringBuilder ("ar cru ");
						var static_ofile = Path.Combine (archOutputDirectory, output_file);
						archive_options.Append (static_ofile).Append (" ");
						lipo_files.Add (static_ofile);
						foreach (var objfile in object_files)
							archive_options.Append (objfile).Append (" ");
						if (!Xcrun (archive_options, out exitCode))
							return exitCode;
						break;
					default:
						throw ErrorHelper.CreateError (99, "Internal error: invalid compilation target {0}. Please file a bug report with a test case (https://github.com/mono/Embeddinator-4000/issues).", CompilationTarget);
					}
				}

				// Create the per-sdk output file
				var sdk_output_file = Path.Combine (OutputDirectory, build_info.Sdk, output_file);
				if (!Lipo (lipo_files, sdk_output_file, out exitCode))
					return exitCode;

				// Call mtouch if we're building a framework
				if (CompilationTarget == CompilationTarget.Framework) {
					switch (Platform) {
					case Platform.macOS:
						throw new NotImplementedException ("frameworks for macOS");
					case Platform.iOS:
					case Platform.tvOS:
					case Platform.watchOS:
						var mtouch = new StringBuilder ();
						var appdir = Path.GetFullPath (Path.Combine (OutputDirectory, build_info.Sdk, "appdir"));
						mtouch.Append (build_info.IsSimulator ? "--sim " : "--dev ");
						mtouch.Append ($"{Quote (appdir)} ");
						mtouch.Append ($"--abi={string.Join (",", build_info.Architectures)} ");
						mtouch.Append ($"--sdkroot {XcodeApp} ");
						mtouch.Append ($"--targetver {build_info.MinVersion} ");
						mtouch.Append ("--dsym:false ");
						mtouch.Append ($"--embeddinator ");
						foreach (var asm in Assemblies)
							mtouch.Append (Quote (Path.GetFullPath (asm.Location))).Append (" ");
						mtouch.Append ($"-r:{GetPlatformAssembly ()} ");
						mtouch.Append ($"--sdk {GetSdkVersion (build_info.Sdk.ToLower ())} ");
						mtouch.Append ("--nostrip ");
						mtouch.Append ("--nolink "); // FIXME: make the mtouch linker not strip away the root assemblies we're passing it.
						mtouch.Append ("--registrar:static ");
						mtouch.Append ($"--cache {Quote (Path.GetFullPath (Path.Combine (outputDirectory, build_info.Sdk, "mtouch-cache")))} ");
						if (Debug)
							mtouch.Append ("--debug ");
						mtouch.Append ($"--assembly-build-target=@all=framework={LibraryName}.framework ");
						mtouch.Append ($"--target-framework {GetTargetFramework ()} ");
						mtouch.Append ($"\"--gcc_flags=-force_load {Path.GetFullPath (sdk_output_file)}\" ");
						if (!RunProcess ("/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/bin/mtouch", mtouch.ToString (), out exitCode))
							return exitCode;

						// Add the headers to the framework
						var fwdir = Path.Combine (OutputDirectory, build_info.Sdk, "appdir", "Frameworks", $"{LibraryName}.framework");
						var headers = Path.Combine (fwdir, "Headers");
						Directory.CreateDirectory (headers);
						foreach (var header in Directory.GetFiles (OutputDirectory, "*.h")) {
							string target = Path.GetFileName (header);
							if (target == "bindings.h")
								target = LibraryName + ".h";
							File.Copy (header, Path.Combine (headers, target), true);
						}
						// Move the framework to the output directory
						var fwpath = Path.Combine (OutputDirectory, build_info.Sdk, $"{LibraryName}.framework");
						if (Directory.Exists (fwpath))
							Directory.Delete (fwpath, true);
						Directory.Move (fwdir, fwpath);
						Directory.Delete (appdir, true); // We don't need this anymore.
						break;
					default:
						throw ErrorHelper.CreateError (99, "Internal error: invalid platform {0}. Please file a bug report with a test case (https://github.com/mono/Embeddinator-4000/issues).", Platform);
					}
				}
			}

			var output_path = Path.Combine (OutputDirectory, output_file);
			if (!Lipo (lipo_files, output_path, out exitCode))
				return exitCode;

			if (!DSymUtil (output_path, out exitCode))
				return exitCode;

			if (build_infos.Length == 2 && CompilationTarget == CompilationTarget.Framework) {
				var dev_fwpath = Path.Combine (OutputDirectory, build_infos [0].Sdk, $"{LibraryName}.framework");
				var sim_fwpath = Path.Combine (OutputDirectory, build_infos [1].Sdk, $"{LibraryName}.framework");
				if (!MergeFrameworks (Path.Combine (OutputDirectory, LibraryName + ".framework"), dev_fwpath, sim_fwpath, out exitCode))
					return exitCode;
			}

			return 0;
		}

		// All files from both frameworks will be included.
		// For files present in both frameworks:
		// * The executables will be lipoed
		// * For any other files the deviceFramework version will win.
		static bool MergeFrameworks (string output, string deviceFramework, string simulatorFramework, out int exitCode)
		{
			var name = Path.GetFileNameWithoutExtension (deviceFramework);
			var deviceFiles = Directory.GetFiles (deviceFramework, "*", SearchOption.AllDirectories);
			var simulatorFiles = Directory.GetFiles (simulatorFramework, "*", SearchOption.AllDirectories);

			Directory.CreateDirectory (output);
			var executables = new List<string> ();
			executables.Add (Path.Combine (deviceFramework, name));
			executables.Add (Path.Combine (simulatorFramework, name));
			if (!Lipo (executables, Path.Combine (output, name), out exitCode))
				return false;
			
			foreach (var file in deviceFiles) {
				if (Path.GetFileName (file) == name)
					continue; // Skip the executable
				var relativePath = file.Substring (deviceFramework.Length);
				if (relativePath [0] == Path.DirectorySeparatorChar)
					relativePath = relativePath.Substring (1);
				var targetPath = Path.Combine (output, relativePath);
				Directory.CreateDirectory (Path.GetDirectoryName (targetPath));
				File.Copy (file, targetPath, true);
			}

			foreach (var file in simulatorFiles) {
				if (Path.GetFileName (file) == name)
					continue; // Skip the executable
				var relativePath = file.Substring (simulatorFramework.Length);
				if (relativePath [0] == Path.DirectorySeparatorChar)
					relativePath = relativePath.Substring (1);
				var targetPath = Path.Combine (output, relativePath);
				if (File.Exists (targetPath))
					continue;
				Directory.CreateDirectory (Path.GetDirectoryName (targetPath));
				File.Copy (file, targetPath, true);
			}

			return true;
		}

		static string GetSdkVersion (string sdk)
		{
			int exitCode;
			string output;
			if (!RunProcess ("xcrun", $"--show-sdk-version --sdk {sdk}", out exitCode, out output))
				throw ErrorHelper.CreateError (7, $"Could not get the sdk version for '{sdk}'");
			return output.Trim ();
		}

		static string GetPlatformAssembly ()
		{
			switch (Platform) {
			case Platform.macOS:
				throw new NotImplementedException ("platform assembly for macOS"); // We need to know full/mobile
			case Platform.iOS:
				return "/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/lib/mono/Xamarin.iOS/Xamarin.iOS.dll";
			case Platform.tvOS:
				return "/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/lib/mono/Xamarin.TVOS/Xamarin.TVOS.dll";
			case Platform.watchOS:
				return "/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/lib/mono/Xamarin.WatchOS/Xamarin.WatchOS.dll";
			default:
				throw ErrorHelper.CreateError (99, "Internal error: invalid platform {0}. Please file a bug report with a test case (https://github.com/mono/Embeddinator-4000/issues).", Platform);
			}
		}

		static string GetTargetFramework ()
		{
			switch (Platform) {
			case Platform.macOS:
				throw new NotImplementedException ("target framework for macOS");
			case Platform.iOS:
				return "Xamarin.iOS,v1.0";
			case Platform.tvOS:
				return "Xamarin.TVOS,v1.0";
			case Platform.watchOS:
				return "Xamarin.WatchOS,v1.0";
			default:
				throw ErrorHelper.CreateError (99, "Internal error: invalid platform {0}. Please file a bug report with a test case (https://github.com/mono/Embeddinator-4000/issues).", Platform);
			}
		}

		static bool RunProcess (string filename, string arguments, out int exitCode, out string stdout)
		{
			Console.WriteLine ($"\t{filename} {arguments}");
			using (var p = new Process ()) {
				p.StartInfo.FileName = filename;
				p.StartInfo.Arguments = arguments;
				p.StartInfo.UseShellExecute = false;
				p.StartInfo.RedirectStandardOutput = true;
				p.Start ();
				stdout = p.StandardOutput.ReadToEnd ();
				exitCode = p.ExitCode;
				return exitCode == 0;
			}
		}

		static int RunProcess (string filename, string arguments)
		{
			Console.WriteLine ($"\t{filename} {arguments}");
			using (var p = Process.Start (filename, arguments)) {
				p.WaitForExit ();
				return p.ExitCode;
			}
		}

		static bool RunProcess (string filename, string arguments, out int exitCode)
		{
			exitCode = RunProcess (filename, arguments);
			return exitCode == 0;
		}

		static bool Xcrun (StringBuilder options, out int exitCode)
		{
			return RunProcess ("xcrun", options.ToString (), out exitCode);
		}

		static bool DSymUtil (string input, out int exitCode)
		{
			exitCode = 0;

			if (!Debug)
				return true;

			string output;
			switch (CompilationTarget) {
			case CompilationTarget.StaticLibrary:
			case CompilationTarget.Framework:
				return true;
			case CompilationTarget.SharedLibrary:
				output = input + ".dSYM";
				break;
			default:
				throw ErrorHelper.CreateError (99, "Internal error: invalid compilation target {0}. Please file a bug report with a test case (https://github.com/mono/Embeddinator-4000/issues).", CompilationTarget);
			}

			var dsymutil_options = new StringBuilder ("dsymutil ");
			dsymutil_options.Append (Quote (input)).Append (" ");
			dsymutil_options.Append ($"-o {Quote (output)} ");
			return Xcrun (dsymutil_options, out exitCode);
		}

		static bool Lipo (List<string> inputs, string output, out int exitCode)
		{
			Directory.CreateDirectory (Path.GetDirectoryName (output));
			if (inputs.Count == 1) {
				File.Copy (inputs [0], output, true);
				exitCode = 0;
				return true;
			} else {
				var lipo_options = new StringBuilder ("lipo ");
				foreach (var file in inputs)
					lipo_options.Append (file).Append (" ");
				lipo_options.Append ("-create -output ");
				lipo_options.Append (Quote (output));
				return Xcrun (lipo_options, out exitCode);
			}
		}

		public static string Quote (string f)
		{
			if (f.IndexOf (' ') == -1 && f.IndexOf ('\'') == -1 && f.IndexOf (',') == -1 && f.IndexOf ('$') == -1)
				return f;

			var s = new StringBuilder ();

			s.Append ('"');
			foreach (var c in f) {
				if (c == '"' || c == '\\')
					s.Append ('\\');

				s.Append (c);
			}
			s.Append ('"');

			return s.ToString ();
		}
	}
}
