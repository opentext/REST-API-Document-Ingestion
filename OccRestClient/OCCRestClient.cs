using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;

namespace CaptureCenter.RestClient
{
    /// <summary>
    /// This sample class demonstrates how to use the OCC rest api for batch ingestion.
    /// The class provides two functions, one to login and the other to create a batch 
    /// from a set of document.
    /// 
    /// The sample code uses the .net HttpClient class for communication.
    /// </summary>
    /// 
    public class OccRestClient : IDisposable
    {
        #region Initialization, properties and private members
        /// <summary>
        /// The public members need to be set according to the OCC system you are using.
        /// </summary>
        public string OccServer { get; set; }   // Url of OCC server
        public string Username { get; set; }
        public string Password { get; set; }
        public int OccSessionTimeout { get; set; } = 20; // Timeout set on the server in minutes
        public int MillisecondsPerMegabyte { get; set; } = 2000;
        public bool SupportsBatchCreationStateInquiry { get; set; } = false;

        public class OCCRestException : Exception
        {
            public OCCRestException(Exception e) : base(e.Message) { }
            public OCCRestException(string message) : base(message) { }
            protected OCCRestException(SerializationInfo info, StreamingContext ctxt) : base(info, ctxt) { }
            public string BatchId { get; set; } = null;
            public string Context { get; set; } = null;
            public string OCCErrorId { get; set; } = null;
        }

        private HttpClient httpClient = null;
        private TimeSpan defaultCancelationTime;
        private CookieContainer cookies = new CookieContainer();

        private void initializeHttpClient()
        {
             HttpClientHandler clientHandler = new HttpClientHandler() { CookieContainer = cookies };

            httpClient = new HttpClient(clientHandler);
            httpClient.BaseAddress = createUrl(OccServer, "api/v1/");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            defaultCancelationTime = httpClient.Timeout;    // go with httpClient's default
            httpClient.Timeout = Timeout.InfiniteTimeSpan;  // disable httpClient's global timeout mechanism

            // In case the system does not have a valid certificate and runs https
            // System.Net.ServicePointManager.ServerCertificateValidationCallback +=
            //        (sender, cert, chain, sslPolicyErrors) => true;
        }
        #endregion

        #region Login
        /// <summary>
        /// Login should be called before any access to OCC, this is obvious. 
        /// OCC logs off the current after some time in the area of 20 minutes.
        /// Therefore, each other call to OCC (in this class only CreateBatch)
        /// should call assertLogin, to check the login status and to re-login if needed.
        /// 
        /// OCC has a three step login sequence. In step one we ask OCC for the path to OTDS
        /// In the second step  we need to aquire a token from OTDS. In the third step we log 
        /// into OCC using this token.
        /// 
        /// OCC maintains its own security token in a cookie that is nicely managed
        /// by the HttpClient class. 
        /// </summary>
        public void Login()
        {
            if (httpClient != null)
            {
                httpClient.Dispose();
                httpClient = null;
            }
            initializeHttpClient();

            string otdsBasePath = getOtdsPath();            // 1. Get OTDS base path
            string ticket = getOtdsTicket(otdsBasePath);    // 2. Get OTDS ticket
            occLogin(ticket);                               // 3. Log into OCC

            string xsrfToken = cookies.GetCookies(httpClient.BaseAddress)
                .Cast<Cookie>()
                .FirstOrDefault(cookie => cookie.Name == "XSRF-TOKEN")
                .Value;
            httpClient.DefaultRequestHeaders.Add("X-XSRF-TOKEN", xsrfToken);
            //getAccountInfo();
        }

        private string getOtdsPath()
        {
            string s = restGet("account/otdsPath", "Get OTDS base path from OCC");
            JObject jo = JObject.Parse(s);
            return (string)jo["otdsPath"];
        }

        private string getOtdsTicket(string otdsBasePath)
        {
            Uri url = createUrl(otdsBasePath, "v1/authentication/credentials");
 
            JObject jo = new JObject(
                new JProperty("user_name", Username),
                new JProperty("password", Password));
            
            using (HttpContent content = new StringContent(jo.ToString()))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = content;

                using (HttpResponseMessage response = httpClient.PostAsync(url, content).Result)
                {
                    verifyResponse(response, "Login to OTDS");
                    string result = response.Content.ReadAsStringAsync().Result;
                    jo = JObject.Parse(result);
                    return (string)jo["ticket"];
                }
            }
        }

        private void occLogin(string otdsTicket)
        {
            string path = "account/otdsLogin";

            var payload = new Dictionary<string, string>() { { "OTDSTicket", otdsTicket } };
            using (HttpContent content = new FormUrlEncodedContent(payload))
                restPost(path, content, "OCC login");
        }

        // Verify connection status if timeout may have occured. 
        // Just call some ping-type function (getAccountInfo) to refresh timeout in OCC.
        // Re-login if needed.
        private DateTime lastAssertion = new DateTime(1, 1, 1);
        private void assertLogin()
        {
            if (httpClient == null) throw new Exception("Not logged in");

            if ((DateTime.Now - lastAssertion).Minutes > OccSessionTimeout - 5)
                try
                {
                    getAccountInfo();
                    lastAssertion = DateTime.Now;
                }
                catch { Login(); }
        }

        private OccAccountInfo getAccountInfo()
        {
            string result = restGet("account/currentUser", "Reading account information");
            return JsonConvert.DeserializeObject<OccAccountInfo>(result);
        }

        class OccAccountInfo
        {
            public string userName { get; set; }
            public string displayName { get; set; }
            public string useProductionScan { get; set; }
            public string useMonitor { get; set; }
            public string useValidation { get; set; }
        }
        #endregion

        #region Batch creation
        /// <summary>
        /// There are two ways to create a batch. With looseFiles on there all files will be attached
        /// to the rootNode in OCC's data pool. Document building is then typically done by the OCC profile.
        /// In document mode the function will create one document for each input file. Of course,
        /// one can also add several files to a document.
        /// </summary>
        public string CreateBatch(
            string profile, List<string> files, 
            bool looseFiles = false, string documentClass = null, string batchname = "noname")
        {
            assertLogin();

            // Create the batch
            createBatch(out string batchID, out string currentOperationID, profile, looseFiles, batchname);

            // Add all files, either to the batch itself or create one document per file
            try
            {
                if (looseFiles)
                {
                    // Attach all files in one REST call
                    attachFilesToBatch(batchID, files);
                }
                else
                {
                    // Attach the files piecemeal
                    foreach (string file in files)
                    {
                        string documentID = createDocument(batchID, documentClass);
                        attachFileToDocument(batchID, documentID, file);
                    }
                }
            }
            catch (Exception e)
            {
                // If something went wrong we want to put the batch into break mode, 
                // so it will be removed by OCC's cleanup service
                try { breakOperation(batchID, currentOperationID, e.Message, e.ToString()); }
                catch (Exception ee) { }

                throw;
            }

            // Close the batch so recognition can start. In case of error we deliver the batch id
            // to the caller so he can check whether the batch was ingested successfully or the error
            // was due to connection failure.
            try { closeOperation(batchID, currentOperationID); }
            catch (Exception e)
            {
                if (SupportsBatchCreationStateInquiry)
                    try { if (isImportDone(batchID)) return batchID; }
                    catch (Exception ex) { }

                throw new OCCRestException(e)
                {
                    BatchId = batchID,
                    Context = "Closing batch failure",
                    OCCErrorId = (e is OCCRestException) ? ((OCCRestException)e).OCCErrorId : "noOccError",
                };
            }
            return batchID;
        }

        private void createBatch(
            out string batchID, out string  currentOperationID,
            string profile, bool looseFiles, string batchname)
        {
            string path = $"batches?profileName={profile}&batchName={batchname}&batchFormat="
                + (looseFiles ? "looseFilesInput" : "documentFilesInput");
            string result = restPost(path, "Creating batch");

            JObject jo = JObject.Parse(result);
            batchID = (string)jo["batchID"];
            currentOperationID = (string)jo["currentOperationID"];
        }

        private string createDocument(string batchID, string documentClass = null)
        {
            string path = $"batches/{batchID}/documentFilesInput/inputDocuments" +
               (documentClass != null ? $"?documentClassName={documentClass}" : null);
            string result = restPost(path, "Creating document");

            JObject jo = JObject.Parse(result);
            return (string)jo["inputDocumentID"];
        }

        private void attachFilesToBatch(string batchID, List<string> filenames)
        {
            string path = $"batches/{batchID}/looseFilesInput/inputFiles";
            attachFiles(path, filenames);
        }

        private void attachFileToDocument(string batchID, string documentID, string filename)
        {
            string path = $"batches/{batchID}/documentFilesInput/inputDocuments/{documentID}/inputFiles";
            attachFiles(path, new List<string>() { filename });
        }

        private void attachFiles(string path, List<string> filenames)
        {
            using (var content = new MultipartFormDataContent())
            {
                long playloadSize = 0;

                foreach (string filename in filenames)
                {
                    // Create content
                    StreamContent imageContent = new StreamContent(File.OpenRead(filename));
                    imageContent.Headers.ContentType = MediaTypeHeaderValue.Parse(MimeMapping.GetMimeMapping(filename));
                    content.Add(imageContent, "files", filename);
                    playloadSize += (new FileInfo(filename)).Length;
                }

                // Calculate time needed to upload
                long expectedTimeInMilliseconds = playloadSize / (1024 * 1024) * MillisecondsPerMegabyte;

                restPost(path, content, "Attaching files " + filenames[0] + "...", expectedTimeInMilliseconds);
            }
        }

        private void closeOperation(string batchID, string operationID)
        {
            string path = $"batches/{batchID}/operations/{operationID}/closeAndSubmitAction";
            restPost(path, "Closing batch");
        }

        private void breakOperation(string batchID, string operationID, string errorMessage = "unknown error", string errorDetails = "unkonwn error details")
        {
           string path = $"batches/{batchID}/operations/{operationID}/breakAction?";

            JObject jo = new JObject(
                new JProperty("errorMessage", errorMessage),
                new JProperty("errorDetails", errorDetails));

            StringContent sc = new StringContent(jo.ToString());
            sc.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            restPost(path, sc, "Breaking batch");
        }

        private bool isImportDone(string batchID)
        {
            string path = $"batches/{batchID}/batchCreationState";
            string result = restGet(path);
            JObject jo = JObject.Parse(result);
            return (string)jo["isImportDone"] == "True";
        }
        #endregion

        #region Other OCC calls
        public List<string> GetDocumentClasses(string profilename)
        {
            List<string> result = new List<string>();

            string path = $"profiles/{profilename}/documentClasses";
            string r = restGet(path, "Retrieving all document classes");
            foreach (JObject jo in JObject.Parse(r)["entries"])
                result.Add((string)jo["documentClassName"]);

            return result;
        }

        public void DeleteBatch(string batchId)
        {
            string path = $"batches/{batchId}";
            string r = restDelete(path, $"Deleting batch {batchId}");
        }
        #endregion

        #region Basic rest methods
        // Shoot off an HTTP Get, handle errors and return string result
        private string restGet(string path, string errContext = null)
        {
            using (HttpResponseMessage response = restCall(HttpMethod.Get, path, null, defaultCancelationTime))
            {
                verifyResponse(response, errContext);
                return response.Content.ReadAsStringAsync().Result;
            }
        }

        private string restPost(string path, string errContext = null)
        {
            return restPost(path, null, errContext);
        }

        // Shoot off an HTTP Post, handle errors and return string result
        private string restPost(string path, HttpContent content, string errContext = null, long expectedTimeInMilliseconds = 0)
        {
            TimeSpan ts = TimeSpan.FromMilliseconds(expectedTimeInMilliseconds);
            TimeSpan expectedTime = ts > defaultCancelationTime ? ts : defaultCancelationTime;

            using (HttpResponseMessage response = restCall(HttpMethod.Post, path, content, expectedTime))
            {
                verifyResponse(response, errContext);
                return response.Content.ReadAsStringAsync().Result;
            }
        }

        private string restDelete(string path, string errContext = null)
        {
            using (HttpResponseMessage response = restCall(HttpMethod.Delete, path, null, defaultCancelationTime))
            {
                verifyResponse(response, errContext);
                return response.Content.ReadAsStringAsync().Result;
            }
        }

        // Make REST call with timeout handling
        private HttpResponseMessage restCall(HttpMethod method, string path, HttpContent content, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(timeout);
                HttpRequestMessage message = new HttpRequestMessage(method, path);
                message.Content = content;
                return httpClient.SendAsync(message, cts.Token).Result;
            }
        }

        // Throw exception in case something went wrong
        private void verifyResponse(HttpResponseMessage response, string errContext = null)
        {
            if (!response.IsSuccessStatusCode)
            {
                string result = response.Content.ReadAsStringAsync().Result;
                throw new OCCRestException(string.IsNullOrEmpty(response.ReasonPhrase) ? result : response.ReasonPhrase)
                {
                    Context = errContext,
                    OCCErrorId = getOccErrorId(result),
                };
            }
        }

        private string getOccErrorId(string result)
        {
            try
            {
                JObject jo = JObject.Parse(result);
                return (string)jo["errorInfo"]["id"];
            }
            catch { return "UnkownOccError"; }
        }

        private Uri createUrl(string server, string path = null)
        {
            Uri url = new Uri(server);
            if (path != null) url = new Uri(url, path);
            return url;
        }
        #endregion 

        #region Dispose
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        private bool _disposed = false;
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing && httpClient != null) httpClient.Dispose();
                _disposed = true;
            }
        }
        #endregion
    }
}
