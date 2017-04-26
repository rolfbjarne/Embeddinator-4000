using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Embeddinator;

using Xamarin;
using DriverTest;

using NUnit.Framework;

namespace ExecutionTests
{
	[TestFixture]
	public class ManagedTest
	{
		[Test]
		public void macOS ()
		{
			TestXamarinMac (Platform.macOS);
		}

		[Test]
		public void macOSModern ()
		{
			TestXamarinMac (Platform.macOSModern);
		}

		[Test]
		public void macOSSystem ()
		{
			TestXamarinMac (Platform.macOSSystem);
		}

		[Test]
		public void macOSFull ()
		{
			TestXamarinMac (Platform.macOSFull);
		}

		[Test]
		public void iOS ()
		{
			TestXamarinMac (Platform.iOS);
		}

		void TestXamarinMac (Platform platform)
		{
			string dllname;
			string dlldir;
			string test_destination = string.Empty;
			string abi;
			List<string> defines = new List<string> ();

			switch (platform) {
			case Platform.macOSFull:
				dlldir = "macos-full";
				dllname = "managed-macos-full.dll";
				defines.Add ("XAMARIN_MAC=1");
				defines.Add ("XAMARIN_MAC_FULL=1");
				abi = "x86_64"; // FIXME: fat XM apps not supported yet
				break;
			case Platform.macOSSystem:
				dlldir = "macos-system";
				dllname = "managed-macos-system.dll";
				defines.Add ("XAMARIN_MAC=1");
				defines.Add ("XAMARIN_MAC_SYSTEM=1");
				abi = "x86_64"; // FIXME: fat XM apps not supported yet
				break;
			case Platform.macOSModern:
				dlldir = "macos-modern";
				dllname = "managed-macos-modern.dll";
				defines.Add ("XAMARIN_MAC=1");
				defines.Add ("XAMARIN_MAC_MODERN=1");
				abi = "x86_64"; // FIXME: fat XM apps not supported yet
				break;
			case Platform.macOS:
				dlldir = "generic";
				dllname = "managed.dll";
				abi = "i386,x86_64";
				break;
			case Platform.iOS:
				dlldir = "ios";
				dllname = "managed-ios.dll";
				defines.Add ("XAMARIN_IOS=1");
				test_destination = "-destination 'platform=iOS Simulator,name=iPhone 6,OS=latest'";
				abi = "armv7,arm64,i386,x86_64";
				break;
			default:
				throw new NotImplementedException ();
			}
			defines.Add ("TEST_FRAMEWORK=1");

			var tmpdir = Cache.CreateTemporaryDirectory ();
			var dll_path = Path.Combine (XcodeProjectGenerator.TestsRootDirectory, "managed", dlldir, "bin", "Debug", dllname);

			// This will build all the managed.dll variants, which is easier than calculating the relative path _as the makefile sees it_ to pass as the target.
			Asserts.RunProcess ("make", $"all -C {Embedder.Quote (Path.Combine (XcodeProjectGenerator.TestsRootDirectory, "managed"))}", "build " + Path.GetFileName (dll_path));

			var outdir = tmpdir + "/out";
			var projectName = "foo";
			Asserts.Generate ("generate", "--debug", dll_path, "-c", "--outdir=" + outdir, "--target=framework", "--platform=" + platform, $"--abi={abi}");

			var framework_path = Path.Combine (outdir, Path.GetFileNameWithoutExtension (dll_path) + ".framework");
			var projectDirectory = XcodeProjectGenerator.Generate (platform, tmpdir, projectName, framework_path, defines: defines.ToArray ());

			Asserts.RunProcess ("xcodebuild", $"test -project {Embedder.Quote (projectDirectory)} -scheme Tests {test_destination}", "run xcode tests");
		}
	}

	public static class XcodeProjectGenerator
	{
		public static string Generate (Platform platform, string outputDirectory, string projectName, string framework_reference_path, string [] defines = null)
		{
			switch (platform) {
			case Platform.macOS:
			case Platform.macOSFull:
			case Platform.macOSModern:
			case Platform.macOSSystem:
				return GenerateMac (outputDirectory, projectName, framework_reference_path, defines);
			case Platform.iOS:
				return GenerateiOS (outputDirectory, projectName, framework_reference_path, defines);
			default:
				throw new NotImplementedException ();
			}
		}
		public static string GenerateMac (string outputDirectory, string projectName, string framework_reference_path, string [] defines = null)
		{
			var projectDirectory = Path.Combine (outputDirectory, $"{projectName}.xcodeproj");
			Directory.CreateDirectory (projectDirectory);

			var sourceDirectory = Path.Combine (outputDirectory, projectName);
			var testDirectory = sourceDirectory + "Tests";
			var asm = typeof (XcodeProjectGenerator).Assembly;
			foreach (var res in asm.GetManifestResourceNames ()) {
				var src_prefix = "objcgentest.xcodetemplate.macos.src.";
				var proj_prefix = "objcgentest.xcodetemplate.macos.proj.";
				var test_prefix = "objcgentest.xcodetemplate.macos.test.";
				if (res.StartsWith (src_prefix, StringComparison.Ordinal)) {
					var relative_path = res.Substring (src_prefix.Length);
					var full_path = Path.Combine (sourceDirectory, relative_path);
					Directory.CreateDirectory (Path.GetDirectoryName (full_path));
					using (var sw = new StreamWriter (full_path))
						asm.GetManifestResourceStream (res).CopyTo (sw.BaseStream);
				} else if (res.StartsWith (test_prefix, StringComparison.Ordinal)) {
					var relative_path = res.Substring (test_prefix.Length);
					var full_path = Path.Combine (testDirectory, relative_path);
					Directory.CreateDirectory (Path.GetDirectoryName (full_path));
					using (var sw = new StreamWriter (full_path))
						asm.GetManifestResourceStream (res).CopyTo (sw.BaseStream);
				} else if (res.StartsWith (proj_prefix, StringComparison.Ordinal)) {
					var relative_path = res.Substring (proj_prefix.Length);
					var full_path = Path.Combine (projectDirectory, relative_path).Replace ("project-name", projectName);
					Directory.CreateDirectory (Path.GetDirectoryName (full_path));
					using (var sw = new StreamWriter (full_path))
						asm.GetManifestResourceStream (res).CopyTo (sw.BaseStream);
					ProcessFile (full_path, projectName, framework_reference_path: framework_reference_path, defines: defines);
				} else {
					Console.WriteLine ("Resource not matched: {0}", res);
				}
			}
			return projectDirectory;
		}

		public static string GenerateiOS (string outputDirectory, string projectName, string framework_reference_path, string [] defines = null)
		{
			var projectDirectory = Path.Combine (outputDirectory, $"{projectName}.xcodeproj");
			Directory.CreateDirectory (projectDirectory);

			var sourceDirectory = Path.Combine (outputDirectory, projectName);
			var testDirectory = sourceDirectory + "Tests";
			var asm = typeof (XcodeProjectGenerator).Assembly;
			foreach (var res in asm.GetManifestResourceNames ()) {
				var src_prefix = "objcgentest.xcodetemplate.ios.src.";
				var proj_prefix = "objcgentest.xcodetemplate.ios.proj.";
				var test_prefix = "objcgentest.xcodetemplate.ios.test.";
				if (res.StartsWith (src_prefix, StringComparison.Ordinal)) {
					var relative_path = res.Substring (src_prefix.Length);
					var full_path = Path.Combine (sourceDirectory, relative_path);
					Directory.CreateDirectory (Path.GetDirectoryName (full_path));
					using (var sw = new StreamWriter (full_path))
						asm.GetManifestResourceStream (res).CopyTo (sw.BaseStream);
				} else if (res.StartsWith (test_prefix, StringComparison.Ordinal)) {
					var relative_path = res.Substring (test_prefix.Length);
					var full_path = Path.Combine (testDirectory, relative_path);
					Directory.CreateDirectory (Path.GetDirectoryName (full_path));
					using (var sw = new StreamWriter (full_path))
						asm.GetManifestResourceStream (res).CopyTo (sw.BaseStream);
				} else if (res.StartsWith (proj_prefix, StringComparison.Ordinal)) {
					var relative_path = res.Substring (proj_prefix.Length);
					var full_path = Path.Combine (projectDirectory, relative_path).Replace ("project-name", projectName);
					Directory.CreateDirectory (Path.GetDirectoryName (full_path));
					using (var sw = new StreamWriter (full_path))
						asm.GetManifestResourceStream (res).CopyTo (sw.BaseStream);
					ProcessFile (full_path, projectName, framework_reference_path: framework_reference_path, defines: defines);
				} else {
					Console.WriteLine ("Resource not matched: {0}", res);
				}
			}
			return projectDirectory;
		}

		static void ProcessFile (string filename, string project_name, string framework_reference_path = null, string dylib_reference_path = null, string [] defines = null)
		{
			var contents = File.ReadAllText (filename);
			contents = contents.Replace ("%TESTS_ROOT_DIR%", Path.GetFullPath (TestsRootDirectory));
			contents = contents.Replace ("%PROJECT_NAME%", project_name);
			if (!string.IsNullOrEmpty (framework_reference_path)) {
				contents = contents.Replace ("%FRAMEWORK_REFERENCE_NAME%", Path.GetFileNameWithoutExtension (framework_reference_path));
				contents = contents.Replace ("%FRAMEWORK_REFERENCE_DIR%", Path.GetFullPath (Path.GetDirectoryName (framework_reference_path)));
			}
			if (!string.IsNullOrEmpty (dylib_reference_path)) {
				contents = contents.Replace ("%DYLIB_REFERENCE_NAME%", Path.GetFileName (dylib_reference_path));
				contents = contents.Replace ("%DYLIB_REFERENCE_DIR%", Path.GetFullPath (Path.GetDirectoryName (dylib_reference_path)));
			}
			if (defines?.Length > 0) {
				contents = contents.Replace ("%GCC_PREPROCESSOR_DEFINITIONS%", string.Join ("\n\t\t\t\t\t\t\t", defines.Select ((v) => "\"" + v + "\",")));
			} else {
				contents = contents.Replace ("%GCC_PREPROCESSOR_DEFINITIONS%", "");
			}


			File.WriteAllText (filename, contents);
		}

		public static string TestsRootDirectory {
			get {
				var dir = Path.GetDirectoryName (System.Reflection.Assembly.GetExecutingAssembly ().Location);
				while (dir.Length > 1 && Path.GetFileName (dir) != "tests")
					dir = Path.GetDirectoryName (dir);
				return dir;
			}
		}
	}
}
