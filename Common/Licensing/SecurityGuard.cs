using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Common.Licensing;

public static class SecurityGuard
{
    public static void GenerateKeys(out string publicKey, out string privateKey)
    {
        using var rsa = RSA.Create(2048);
        privateKey = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    public static string SignData(string data, string privateKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);

        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        byte[] signature = rsa.SignData(
            dataBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return Convert.ToBase64String(signature);
    }

    public static bool VerifyData(string data, string signature, string publicKey)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

            byte[] dataBytes = Encoding.UTF8.GetBytes(data);
            byte[] signBytes = Convert.FromBase64String(signature);

            return rsa.VerifyData(
                dataBytes,
                signBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }
}
