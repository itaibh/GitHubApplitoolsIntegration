using GitHubIntegration.Models;
using Newtonsoft.Json;
using Octokit;
using Octokit.Internal;
using System;
using System.IO;
using System.Linq;
using System.Net;
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

        protected override void Initialize(HttpControllerContext controllerContext)
        {
            base.Initialize(controllerContext);

            serializer_ = new SimpleJsonSerializer();
            client_ = new GitHubClient(new ProductHeaderValue("ApplitoolsIntegration"));
            string personalAccessToken = Environment.GetEnvironmentVariable("GITHUB_APPLITOOLS_PAT");
            var tokenAuth = new Credentials(personalAccessToken);
            client_.Credentials = tokenAuth;
        }

        public void Get()
        {

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
                CommitStatusClient csc = new CommitStatusClient(apiConnection);
                NewCommitStatus newCommitStatus = new NewCommitStatus();
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
                        if (VisualTestsPassed_(batchId))
                        {
                            newCommitStatus.Description = "All tests passed";
                            newCommitStatus.State = CommitState.Success;
                        }
                        else
                        {
                            newCommitStatus.Description = "Tests failed";
                            newCommitStatus.State = CommitState.Failure;
                        }
                    }
                }
                catch (Exception)
                {
                    newCommitStatus.Description = "An error has occured";
                    newCommitStatus.State = CommitState.Error;
                }

                CommitStatus commitStatus = csc.Create(sep.Repository.Id, sep.Sha, newCommitStatus).Result;

                return Ok();
            }
            return BadRequest();
        }

        private string GetTargetUrlFromBatchId_(string batchId)
        {
            return string.Format("https://eyes.applitools.com/app/batches/{0}", batchId);
        }

        private bool VisualTestsPassed_(string batchId)
        {
            return true; // TODO - change!
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
            string credentials = (string)Configuration.Properties["APPLITOOLS_SERVER_CREDENTIALS"];
            string url = string.Format("https://eyes.applitools.com/api/sessions/batches/batchId/{0}?format=json&{1}", reference, credentials);
            string json = Get(url);
            BatchIdData batchIdData = JsonConvert.DeserializeObject<BatchIdData>(json);
            return batchIdData.BatchId;
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
