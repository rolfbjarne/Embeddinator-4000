﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

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
			RunManagedTests (Platform.macOS);
		}

		[Test]
		public void macOSModern ()
		{
			RunManagedTests (Platform.macOSModern);
		}

		[Test]
		public void macOSSystem ()
		{
			RunManagedTests (Platform.macOSSystem);
		}

		[Test]
		public void macOSFull ()
		{
			RunManagedTests (Platform.macOSFull);
		}

		[Test]
		public void iOS_simulator ()
		{
			RunManagedTests (Platform.iOS, "-destination 'platform=iOS Simulator,name=iPhone 6,OS=latest'");
		}

		[Test]
		public void iOS_device ()
		{
			if (string.IsNullOrEmpty (iOSDeviceIdentifier))
				Assert.Ignore ("No applicable iOS device connected.");
			
			RunManagedTests (Platform.iOS, $"-destination 'platform=iOS,id={iOSDeviceIdentifier}'");
		}

		XmlDocument device_xml;
		string FindDevice (params string [] valid_classes)
		{
			if (device_xml == null) {
				var cachedir = Cache.CreateTemporaryDirectory ();
				var xmlpath = Path.Combine (cachedir, "devices.xml");
				Asserts.RunProcess ("/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/bin/mlaunch", $"--listdev={Embedder.Quote (xmlpath)} --output-format=xml", "mlaunch --listdev");
				device_xml = new XmlDocument ();
				device_xml.Load (xmlpath);
			}
			var nodes = device_xml.SelectNodes ("/MTouch/Device[" + string.Join (" or ", valid_classes.Select ((v) => "DeviceClass = \"" + v + "\"")) + "]/DeviceIdentifier");
			var devices = new List<string> ();
			foreach (XmlNode node in nodes)
				devices.Add (node.InnerText);
			if (devices.Count == 0)
				return string.Empty;
			
			devices.Sort (StringComparer.Ordinal); // Sord devices all applicable devices so the same test run always runs on the same device.
			return devices [0];
		}

		// A device identifier for an iOS device. Can be either iPhone or iPad. Will be an empty string if no applicable devices are attached.
		string ios_device_identifier;
		string iOSDeviceIdentifier {
			get {
				if (ios_device_identifier == null)
					ios_device_identifier = FindDevice ("iPhone", "iPad");
				return ios_device_identifier;
			}
		}

		void RunManagedTests (Platform platform, string test_destination = "")
		{
			string dllname;
			string dlldir;
			string abi;
			List<string> defines = new List<string> ();
			var managedTestCount = 20; // in Tests.m
			var xamarinMacTestCount = 1; // in macTests.m
			var xamariniOSTestCount = 1; // in iosTests.m

			switch (platform) {
			case Platform.macOSFull:
				dlldir = "macos-full";
				dllname = "managed-macos-full.dll";
				defines.Add ("XAMARIN_MAC=1");
				defines.Add ("XAMARIN_MAC_FULL=1");
				abi = "x86_64"; // FIXME: fat XM apps not supported yet
				managedTestCount += xamarinMacTestCount;
				break;
			case Platform.macOSSystem:
				dlldir = "macos-system";
				dllname = "managed-macos-system.dll";
				defines.Add ("XAMARIN_MAC=1");
				defines.Add ("XAMARIN_MAC_SYSTEM=1");
				abi = "x86_64"; // FIXME: fat XM apps not supported yet
				managedTestCount += xamarinMacTestCount;
				break;
			case Platform.macOSModern:
				dlldir = "macos-modern";
				dllname = "managed-macos-modern.dll";
				defines.Add ("XAMARIN_MAC=1");
				defines.Add ("XAMARIN_MAC_MODERN=1");
				abi = "x86_64"; // FIXME: fat XM apps not supported yet
				managedTestCount += xamarinMacTestCount;
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
				abi = "armv7,arm64,i386,x86_64";
				managedTestCount += xamariniOSTestCount;
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

			string output;
			var builddir = Path.Combine (tmpdir, "xcode-build-dir");
			Asserts.RunProcess ("xcodebuild", $"test -project {Embedder.Quote (projectDirectory)} -scheme Tests {test_destination} CONFIGURATION_BUILD_DIR={Embedder.Quote (builddir)}", out output, "run xcode tests");
			// assert the number of tests passed, so that we can be sure we actually ran all the tests. Otherwise it's very easy to ignore when a bug is causing tests to not be built.
			Assert.That (output, Does.Match ($"Test Suite 'All tests' passed at .*\n\t Executed {managedTestCount} tests, with 0 failures"), "test count");
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
				return Generate ("macos", outputDirectory, projectName, framework_reference_path, defines);
			case Platform.iOS:
				return Generate ("ios", outputDirectory, projectName, framework_reference_path, defines);
			default:
				throw new NotImplementedException ();
			}
		}

		static string Generate (string infix, string outputDirectory, string projectName, string framework_reference_path, string [] defines)
		{
			var projectDirectory = Path.Combine (outputDirectory, $"{projectName}.xcodeproj");
			var sourceDirectory = Path.Combine (outputDirectory, projectName);
			var testDirectory = Path.Combine (outputDirectory, "Tests");
			var asm = typeof (XcodeProjectGenerator).Assembly;

			Directory.CreateDirectory (projectDirectory);

			var prefixes = new [] {
				new { Prefix = $"objcgentest.xcodetemplate.{infix}.src.", Directory = sourceDirectory },
				new { Prefix = $"objcgentest.xcodetemplate.{infix}.proj.", Directory = projectDirectory },
				new { Prefix = $"objcgentest.xcodetemplate.{infix}.test.", Directory = testDirectory },
			};
			foreach (var res in asm.GetManifestResourceNames ()) {
				foreach (var prefix in prefixes) {
					if (!res.StartsWith (prefix.Prefix, StringComparison.Ordinal))
						continue;
					var relative_path = res.Substring (prefix.Prefix.Length);
					var full_path = Path.Combine (prefix.Directory, relative_path);
					Directory.CreateDirectory (Path.GetDirectoryName (full_path));
					using (var sw = new StreamWriter (full_path)) {
						using (var str = asm.GetManifestResourceStream (res))
							str.CopyTo (sw.BaseStream);
					}
					ProcessFile (full_path, projectName, framework_reference_path: framework_reference_path, defines: defines);
					break;
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
