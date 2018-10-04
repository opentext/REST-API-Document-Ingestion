using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OccRestClient
{
    class Program
    {
        private static string occServer = "10.10.10.10";
        private static string username = "someUser";
        private static string password = "secret";
        private static string profile = "Profile1";
        private static List<string> files = new List<string>()
        {
            @"C:\01_Work\01_OCC\Scripting\16 REST API\Img1.tif",
            @"C:\01_Work\01_OCC\Scripting\16 REST API\Img2.tif",
        };

        static void Main(string[] args)
        {
            OccRestClient client = createClient();
            client.Login();
            bool loosePageMode = false;

            for (int i = 0; i != 10; i++)
            {
                DateTime start = DateTime.Now;
                client.CreateBatch(profile, files, loosePageMode, "batch-" + i);
                loosePageMode = !loosePageMode;
                Console.WriteLine("Duration:" + ((int)((DateTime.Now - start).TotalMilliseconds)));
            }
            Console.ReadLine();
        }

        private static OccRestClient createClient()
        {
            return new OccRestClient()
            {
                OccServer = occServer,
                Username = username,
                Password = password,
            };
        }
    }
}
