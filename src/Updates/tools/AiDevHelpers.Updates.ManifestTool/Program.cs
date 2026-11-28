// Release-time helper for the package workflow (unit 10b). Two commands:
//   write  <packageDir> <version> [notAfterIso]  writes manifest.json (never signs)
//   verify <packageDir>                          checks manifest.json.sig against ReleaseSigningKeys (exit 1 on failure)
using AiDevHelpers.Updates;

if (args.Length >= 3 && args[0] == "write")
{
    DateTimeOffset? notAfter = args.Length >= 4 ? DateTimeOffset.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : null;
    UpdateManifestWriter.WriteManifest(args[1], args[2], notAfter);
    Console.WriteLine($"Wrote {Path.Combine(args[1], UpdateManifest.FileName)}");
    return 0;
}

if (args.Length == 2 && args[0] == "verify")
{
    var verifier = new UpdatePackageVerifier(new Ed25519PackageSignatureVerifier([ReleaseSigningKeys.Current()]));
    // Version 0.0.0 as the running version: the release check is the signature and every file hash, not the version order.
    UpdatePackageVerification result = await verifier.VerifyAsync(args[1], "0.0.0", allowDevPackages: false, CancellationToken.None);
    Console.WriteLine($"{result.Status}: {result.Message}");
    return result.IsValid ? 0 : 1;
}

Console.Error.WriteLine("usage: write <packageDir> <version> [notAfterIso] | verify <packageDir>");
return 2;
