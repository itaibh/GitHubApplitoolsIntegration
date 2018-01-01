using Octokit;
using System;
using System.Linq;
using System.Web.Http;
using System.Web.Http.Controllers;

namespace GitHubIntegration.Controllers
{
    public class GitHubController : ApiController
    {
        GitHubClient client_;

        protected override void Initialize(HttpControllerContext controllerContext)
        {
            base.Initialize(controllerContext);

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
            string pullRequestJson = Request.Content.ReadAsStringAsync().Result;
            if (Request.Headers.GetValues("X-Github-Event").Contains("pull_request"))
            {
                PullRequestEventPayload pr = new Octokit.Internal.SimpleJsonSerializer().Deserialize<PullRequestEventPayload>(pullRequestJson);
                return ProcessPullRequest_(pr);
            }
            return BadRequest();
        }

        private IHttpActionResult ProcessPullRequest_(PullRequestEventPayload pr)
        {
            if (pr.Action == "opened" || pr.Action == "reopened")
            {
                IApiConnection apiConnection = new ApiConnection(client_.Connection);
                CommitStatusClient csc = new CommitStatusClient(apiConnection);
                string reference = pr.PullRequest.Head.Sha;
                CommitStatus commitStatus = csc.Create(pr.Repository.Id, reference,
                    new NewCommitStatus
                    {
                        State = CommitState.Success,
                        Description = "The test passed",
                        TargetUrl = "https://eyes.applitools.com"
                    }).Result;

                return Json(commitStatus);
            }
            return BadRequest();
        }
    }
}
