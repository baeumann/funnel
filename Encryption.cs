using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace funnel_
{
    public class Encryption
    {
        public static String CreateDigest(String input) 
        {
            byte[] hashArray = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(input));

            Array.Reverse(hashArray);
            
            BigInteger bigInteger = new BigInteger(hashArray);
            if (bigInteger < 0) {
                return "-" + (-bigInteger).ToString("x").TrimStart('0');
            } else {
                return bigInteger.ToString("x").TrimStart('0');
            }
        }
    }
}