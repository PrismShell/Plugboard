using System.Security.Cryptography;

// Plugboard signing tool. Same crypto API as the host's verifier, so what this
// signs the host will accept. Built as a .NET tool (not PowerShell) because
// Windows PowerShell 5.1 runs on .NET Framework and lacks ExportRSAPrivateKey.
//
//   plugboard-sign gen-keys <outDir>
//   plugboard-sign sign <dll> <privateKeyFile>

if (args.Length == 0)
{
    Console.Error.WriteLine("usage:\n  gen-keys <outDir>\n  sign <dll> <privateKeyFile>");
    return 1;
}

switch (args[0])
{
    case "gen-keys":
    {
        var outDir = args.Length > 1 ? args[1] : ".";
        Directory.CreateDirectory(outDir);
        using var rsa = RSA.Create(3072);
        File.WriteAllText(Path.Combine(outDir, "plugboard-private.key"), Convert.ToBase64String(rsa.ExportRSAPrivateKey()));
        var pub = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        File.WriteAllText(Path.Combine(outDir, "plugboard-public.key"), pub);
        Console.WriteLine(pub);   // also print the public key (for the host's TrustedKeys)
        return 0;
    }
    case "sign":
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: sign <dll> <privateKeyFile>"); return 1; }
        var dll = args[1];
        using var rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(File.ReadAllText(args[2]).Trim()), out _);
        var sig = rsa.SignData(File.ReadAllBytes(dll), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        File.WriteAllText(dll + ".sig", Convert.ToBase64String(sig));
        Console.WriteLine(dll + ".sig");
        return 0;
    }
    default:
        Console.Error.WriteLine("unknown command: " + args[0]);
        return 1;
}
