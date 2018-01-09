using GitHubIntegration.Models;
using Jose;
using Newtonsoft.Json;
using Octokit;
using Octokit.Helpers;
using Octokit.Internal;
using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Web;
using System.Web.Http;
using System.Web.Http.Controllers;

namespace GitHubIntegration.Controllers
{
    public class GitHubController : ApiController
    {
        private GitHubClient client_;
        private SimpleJsonSerializer serializer_;
        private string clientId_ = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_CLIENT_ID");
        private string clientSecret_ = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_CLIENT_SECRET");
        private Application app_ = null;

        protected override void Initialize(HttpControllerContext controllerContext)
        {
            base.Initialize(controllerContext);

            serializer_ = new SimpleJsonSerializer();
            client_ = new GitHubClient(new ProductHeaderValue("ApplitoolsIntegration"));
            string personalAccessToken = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_PAT");

            string pemFilePath = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_PEM_FILE_PATH");
            string pemData = File.ReadAllText(pemFilePath);

            long unixTimeInSeconds = DateTimeOffset.UtcNow.ToUnixTime();
            object payload = new {
                iat = unixTimeInSeconds,
                exp = unixTimeInSeconds + 600,
                iss = 7820
            };

            RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(pemData);

            string token = JWT.Encode(payload, rsa, JwsAlgorithm.RS256);

            client_.Credentials = new Credentials(token, AuthenticationType.Bearer);

            app_ = client_.Application.Create().Result;
        }

        public IHttpActionResult Post()
        {
            string json = Request.Content.ReadAsStringAsync().Result;
            string gitHubEvent = Request.Headers.GetValues("X-Github-Event").First();
            switch (gitHubEvent)
            {
                case "pull_request":
                    PullRequestEventPayload prep = serializer_.Deserialize<PullRequestEventPayload>(json);
                    return ProcessPullRequest_(prep);

                case "status":
                    StatusEventPayload sep = serializer_.Deserialize<StatusEventPayload>(json);
                    return ProcessStatusRequest_(sep);
            }
            return BadRequest();
        }

        private IHttpActionResult ProcessStatusRequest_(StatusEventPayload sep)
        {
            if (sep.Context.StartsWith("continuous-integration"))
            {
                IApiConnection apiConnection = new ApiConnection(client_.Connection);
                NewCommitStatus newCommitStatus = new NewCommitStatus
                {
                    Context = "tests/applitools"
                };
                try
                {
                    string batchId = GetServerBatchIdFromStartInfoBatchId_(sep.Sha);
                    newCommitStatus.TargetUrl = GetTargetUrlFromBatchId_(batchId);

                    if (sep.State.Value == CommitState.Pending)
                    {
                        newCommitStatus.Description = "Test is running";
                        newCommitStatus.State = CommitState.Pending;
                    }
                    else
                    {
                        BatchData batchData = GetBatchResultsSummary_(batchId);
                        if (batchData.UnresolvedCount == 0 && batchData.FailedCount == 0)
                        {
                            newCommitStatus.Description = "All tests passed";
                            newCommitStatus.State = CommitState.Success;
                        }
                        else
                        {
                            newCommitStatus.Description = batchData.ToString();
                            newCommitStatus.State = CommitState.Failure;
                        }
                    }
                }
                catch (Exception)
                {
                    newCommitStatus.Description = "An error has occured";
                    newCommitStatus.State = CommitState.Error;
                }
                CommitStatusClient csc = new CommitStatusClient(apiConnection);
                CommitStatus commitStatus = csc.Create(sep.Repository.Id, sep.Sha, newCommitStatus).Result;

                return Ok();
            }
            return BadRequest();
        }

        private string GetTargetUrlFromBatchId_(string batchId)
        {
            return string.Format("https://eyes.applitools.com/app/batches/{0}", batchId);
        }

        public string Get(string uri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        // TODO - change!
        private string GetServerBatchIdFromStartInfoBatchId_(string reference)
        {
            string credentials = ConfigurationManager.AppSettings["APPLITOOLS_SERVER_CREDENTIALS"];
            string url = string.Format("https://eyes.applitools.com/api/sessions/batches/batchId/{0}?format=json&{1}", reference, credentials);
            string json = Get(url);
            BatchIdData batchIdData = JsonConvert.DeserializeObject<BatchIdData>(json);
            return batchIdData.BatchId;
        }

        // TODO - change!
        private BatchData GetBatchResultsSummary_(string batchId)
        {
            string credentials = ConfigurationManager.AppSettings["APPLITOOLS_SERVER_CREDENTIALS"];
            string url = string.Format("https://eyes.applitools.com/api/sessions/batches?format=json&count=1&limit=%3D%3D+{0}&{1}", batchId, credentials);
            string json = Get(url);
            BatchResultsSummary batchResultsSummary = JsonConvert.DeserializeObject<BatchResultsSummary>(json);
            BatchData batchData = batchResultsSummary.Batches[0];
            return batchData;
        }

        private IHttpActionResult ProcessPullRequest_(PullRequestEventPayload pr)
        {
            if (pr.Action == "opened" || pr.Action == "reopened")
            {
                IApiConnection apiConnection = new ApiConnection(client_.Connection);
                CommitStatusClient csc = new CommitStatusClient(apiConnection);
                string reference = pr.PullRequest.Head.Sha;

                CommitStatus pendingCommitStatus = csc.Create(pr.Repository.Id, reference,
                   new NewCommitStatus
                   {
                       State = CommitState.Pending,
                       Description = "The test is running",
                       TargetUrl = "https://eyes.applitools.com"
                   }).Result;

                Thread.Sleep(2000);

                CommitStatus resultCommitStatus = csc.Create(pr.Repository.Id, reference,
                    new NewCommitStatus
                    {
                        State = CommitState.Success,
                        Description = "The test passed",
                        TargetUrl = "https://eyes.applitools.com"
                    }).Result;

                return Ok();
            }
            return BadRequest();
        }
    }
}
