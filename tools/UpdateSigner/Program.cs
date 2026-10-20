// Signs MxAutoPair update manifests. The private key stays on the publishing PC.
//   keygen <key.pem>       create a P-256 key pair; prints the public key to paste into Program.cs
//   sign <key.pem> <file>  write <file>.sig (base64 ECDSA P-256 / SHA-256 over the exact file bytes)
//   pubkey <key.pem>       print the public key again
using System.Security.Cryptography;

if (args.Length >= 2 && args[0] == "keygen")
{
    if (File.Exists(args[1])) { Console.Error.WriteLine($"{args[1]} already exists; not overwriting it."); return 1; }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}
if (args.Length >= 2 && args[0] == "pubkey")
{
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(args[1]));
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}
if (args.Length >= 3 && args[0] == "sign")
{
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(args[1]));
    var signature = key.SignData(File.ReadAllBytes(args[2]), HashAlgorithmName.SHA256);
    File.WriteAllText(args[2] + ".sig", Convert.ToBase64String(signature));
    Console.WriteLine($"Signed {args[2]}");
    return 0;
}
Console.Error.WriteLine("usage: keygen <key.pem> | sign <key.pem> <file> | pubkey <key.pem>");
return 2;
