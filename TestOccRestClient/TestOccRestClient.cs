using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System.IO;
using System.Threading;

namespace CaptureCenter.RestClient
{
    [TestClass]
    public class TestOccRestClient
    {
        private static string rootfolder = Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..");

        private string occServer = "http://occ.myCompany.com";
        private string username = "SomeUser";
        private string password = "Secret";
        private string profile = "Profile1";
        private string documentClass = "DocumentClass";

        private List<string> files = new List<string>()
        {
            Path.Combine(rootfolder, "Img1.tif"),
            Path.Combine(rootfolder, "Img2.tif"),
        };

        private OccRestClient createClient()
        {
            string settingsFile = Path.Combine(rootfolder, "testSettings.json");
            if (File.Exists(settingsFile))
                return JsonConvert.DeserializeObject<OccRestClient>(File.ReadAllText(settingsFile));
            
            return new OccRestClient()
                {
                    OccServer = occServer,
                    Username = username,
                    Password = password,
                };
        }

        [TestMethod]
        public void CreateSomeBatches()
        {
            OccRestClient client = createClient();
            try
            {
                client.Login();
                bool loosePageMode = false;
                List<string> times = new List<string>();

                for (int i = 0; i != 10; i++)
                {
                    DateTime start = DateTime.Now;
                    client.CreateBatch(profile, files, loosePageMode, documentClass, "batch-" + i);
                    loosePageMode = !loosePageMode;
                    times.Add(((int)((DateTime.Now - start).TotalMilliseconds)).ToString());
                }
            }
            catch (Exception e)
            {
                Type t = e.GetType();
                throw;
            }
        }

        [TestMethod]
        public void SizeTest()
        {
            int chunkSize = 1024 * 1024;
            int size = 0;
            byte[] bytes = new byte[chunkSize];
            List<string> f = new List<string>() { @"c:\temp\xx.tif" };

            List<string> times = new List<string>();

            OccRestClient client = createClient();
            client.Login();

            while (true)
            {
                bool error = false;
                Exception exception; ;

                FileStream fs = File.Open(@"c:\temp\xx.tif", FileMode.Create);
                for (int i = 0; i < Math.Pow(2, size); i++)
                    fs.Write(bytes, 0, chunkSize);
                fs.Close();

                DateTime start = DateTime.Now;
                try
                {
                    client.CreateBatch(profile, f, false, documentClass, "batch-" + size);
                }
                catch (Exception e)
                {
                    error = true;
                    exception = e;
                }
                times.Add(size.ToString() +  " "  + ((int)((DateTime.Now - start).TotalMilliseconds)).ToString());
                if (error)
                    break;

                size++;
            }
        }

        [TestMethod]
        public void TestAndExplore()
        {
            OccRestClient client = createClient();
            client.Login();
            List<string> documentClasses = client.GetDocumentClasses("_xECM_for_SuccessFactors_1");
            client.Login();
            documentClasses = client.GetDocumentClasses("_xECM_for_SuccessFactors_1");
            Thread.Sleep(8000);
            try
            {
                documentClasses = client.GetDocumentClasses("_xECM_for_SuccessFactors_1");
            }
            catch (Exception e)
            {
                client.Login();
                documentClasses = client.GetDocumentClasses("_xECM_for_SuccessFactors_1");
            }
            client.Login();
            
        }
    }
}
