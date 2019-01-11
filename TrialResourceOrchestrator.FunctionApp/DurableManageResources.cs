using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Rest;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ExecutionContext = Microsoft.Azure.WebJobs.ExecutionContext;
using static Microsoft.Azure.Management.Fluent.Azure;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;

namespace TrialResourceOrchestrator.FunctionApp
{

    public static class DurableManageResources
    {

        //private static string key = TelemetryConfiguration.Active.InstrumentationKey = System.Environment.GetEnvironmentVariable("APPINSIGHTS_INSTRUMENTATIONKEY", EnvironmentVariableTarget.Process);
        //private static TelemetryClient telemetry = new TelemetryClient() { InstrumentationKey = key }; 


        [FunctionName("ManageResources")]
        public static async Task ManageResources([OrchestrationTrigger] DurableOrchestrationContext context)
        {
            context.SetCustomStatus("StartingManageResources");
            var templatesWithMetadata = await context.CallActivityAsync<List<BaseTemplate>>("GetTemplatesToManage", null);

            foreach (var template in templatesWithMetadata)
            {
                var count = queueClient().GetQueueReference(template.QueueName).ApproximateMessageCount ?? 0;
                template.CurrentQueueSize = count;
            }
            context.SetCustomStatus("SetItemsToCreate");
            var taskList = new List<Task>();
            taskList.AddRange(templatesWithMetadata.Where(a => a.ItemstoCreate > 0).Select(a => context.CallSubOrchestratorAsync("CreateATrialResource", new TrialResource { TemplateName = a.Name})));
            context.SetCustomStatus("SetItemsToCreate");
            Task.WaitAll(taskList.ToArray());
            context.SetCustomStatus("MissingItemsCreated");
            DateTime nextCleanup = context.CurrentUtcDateTime.AddSeconds(10);
            await context.CreateTimer(nextCleanup, CancellationToken.None);

            context.ContinueAsNew(null);

        }
        [FunctionName("CreateATrialResource")]
        public static async Task CreateATrialResource([OrchestrationTrigger] DurableOrchestrationContext context )
        {
            DateTime start = DateTime.UtcNow;
            TrialResource resource = context.GetInput<TrialResource>();
            context.SetCustomStatus("Started");
            resource.SessionId = Guid.NewGuid();
            resource.InstanceId = context.InstanceId;
            //telemetry.Context.Operation.Id = context.InstanceId;
            //telemetry.Context.Operation.Name = "CreateATrialResource";
            //if (!String.IsNullOrEmpty(resource.AppServiceName))
            //{
            //    telemetry.Context.User.Id = resource.AppServiceName;
            //}
            //telemetry.TrackEvent("DurableManageResources called");
            //telemetry.TrackMetric("Test Metric", DateTime.Now.Millisecond);

            //var outputs = new List<string>();

            resource = await context.CallActivityAsync<TrialResource>("GetSubscriptionAndResourceGroupName", resource); 
            context.SetCustomStatus("ResourceConfigSet");
            resource.InstanceId = context.InstanceId;

            resource = await context.CallActivityAsync<TrialResource>("CreateResourceGroup", resource);
            context.SetCustomStatus("ResourceGroupCreated");
            resource.InstanceId = context.InstanceId;

            resource = await context.CallActivityAsync<TrialResource>("StartDeployment", resource);
            context.SetCustomStatus("DeploymentStarted");
            resource.InstanceId = context.InstanceId;
            resource.DeploymentCheckAttempt = 0;
            while ((resource.Status != ResourceStatus.AvailableForAssignment && resource.Status != ResourceStatus.Error) &&  resource.DeploymentCheckAttempt++ < resource.DeploymentCheckTries)
            {
                SdkContext.DelayProvider.Delay(resource.DeploymentCheckIntervalSeconds * 1000);
                resource = await context.CallActivityAsync<TrialResource>("CheckDeploymentStatus", resource);
                context.SetCustomStatus("DeploymentGoingOn");
                resource.InstanceId = context.InstanceId;
            }
            context.SetCustomStatus("DeploymentCompleted");
            resource.InstanceId = context.InstanceId;

            await queueClient().GetQueueReference(resource.queuename).AddMessageAsync(new CloudQueueMessage(JsonConvert.SerializeObject(resource)));
            //telemetry.TrackDependency( "ARM", "CreateATrialResource", JsonConvert.SerializeObject(resource), start, DateTime.UtcNow-start, true);
            // sleep between cleanups
            //DateTime nextCleanup = context.CurrentUtcDateTime.AddSeconds(10);
            //await context.CreateTimer(nextCleanup, CancellationToken.None);

            //context.ContinueAsNew(new TrialResource { TemplateName = "WordPress" });
            return;

        }

        [FunctionName("CreateResourceGroup")]
        public static async Task<TrialResource> CreateResourceGroup([ActivityTrigger] TrialResource resource, ILogger log)
        {
            try
            {
                log.LogInformation($"Creating ResourceGroup {resource.ResourceGroupName} in {resource.SubscriptionId} region {resource.Region}");

                var armClient = await ArmClient.GetAzureClient(resource);
                    await armClient
                    .WithSubscription(resource.SubscriptionId)
                    .ResourceGroups.Define(resource.ResourceGroupName)
                    .WithRegion(Region.Create(resource.Region))
                    .CreateAsync();

                return resource;
            }
            catch (Exception ex) {
                log.LogError($"Exception creating ResourceGroup: {ex.Message } : {ex.StackTrace} : {ex.InnerException.Message}");
                throw ex;
            }
        }
        [FunctionName("StartDeployment")]
        public static async Task<TrialResource> StartDeployment([ActivityTrigger] TrialResource resource, ILogger log)
        {
            try
            {
                log.LogInformation($"Creating ResourceGroup {resource.ResourceGroupName} in {resource.SubscriptionId}");
                var azure = await ArmClient.GetAzureClient(resource);
                await azure
                        .WithSubscription(resource.SubscriptionId)
                        .Deployments.Define(resource.AppServiceName)
                        .WithExistingResourceGroup(resource.ResourceGroupName)
                        .WithTemplateLink(resource.ARMTemplateLink, null)
                        .WithParameters(JsonConvert.DeserializeObject(resource.ARMParameters))
                        .WithMode(resource.DeploymentMode.ToEnum<DeploymentMode>())
                        .CreateAsync();


                return resource;
            }
            catch (Exception ex)
            {
                log.LogError($"Exception creating ResourceGroup: {ex.Message } : {ex.StackTrace} : {ex.InnerException.Message}");
                throw ex;
            }
        }

        [FunctionName("CheckDeploymentStatus")]
        public static async Task<TrialResource> CheckDeploymentStatus([ActivityTrigger] TrialResource resource, ILogger log)
        {
            try
            {
                log.LogInformation($"Checking Deployment Status {resource.ResourceGroupName} in {resource.SubscriptionId} |Attempt:{resource.DeploymentCheckAttempt}/{resource.DeploymentCheckTries} |Template{resource.TemplateName} |Region:{resource.Region}");
                var azure = await ArmClient.GetAzureClient(resource);

                var deployment = await azure.WithSubscription(resource.SubscriptionId).Deployments.GetByResourceGroupAsync(resource.ResourceGroupName, resource.AppServiceName);
                if (StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Succeeded"))
                {
                    resource.Status = ResourceStatus.AvailableForAssignment;
                }
                else if (StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Failed") || StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Cancelled"))
                {
                    resource.Status = ResourceStatus.Error;
                }
                else
                {
                    resource.Status = ResourceStatus.DeploymentInProgress;
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Exception checking DeploymentStatus: {ex.Message } : {ex.StackTrace} : {ex.InnerException.Message}");
            }
            return resource;
        }
        [FunctionName("GetTemplatesToManage")]
        public static async Task<List<BaseTemplate>> GetTemplatesToManage([ActivityTrigger]string resource, ExecutionContext executionContext)
        {
            List<BaseTemplate> templates = JsonConvert.DeserializeObject<List<BaseTemplate>>(await File.ReadAllTextAsync(System.IO.Path.Combine(executionContext.FunctionAppDirectory, "config", "templates.json")));
            List<Config> config = JsonConvert.DeserializeObject<List<Config>>(await File.ReadAllTextAsync(System.IO.Path.Combine(executionContext.FunctionAppDirectory, "config", "templates.json")));
            foreach (var template in templates)
            {
                template.Subscriptions = config.First(a => a.AppService == template.AppService.ToString()).Subscriptions;
            }
            return templates.ToList();
        }
        // Retrieve storage account from connection string.
        private static CloudStorageAccount storageAccount = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));
        private static CloudQueueClient queueClient()
        {
            // Create the blob client.
            return storageAccount.CreateCloudQueueClient();
        }

        [FunctionName("GetSubscriptionAndResourceGroupName")]
        public static async Task<TrialResource> GetSubscriptionAndResourceGroupName([ActivityTrigger]TrialResource resource, ExecutionContext executionContext)
        {
            List<BaseTemplate> templates = JsonConvert.DeserializeObject<List<BaseTemplate>>(await File.ReadAllTextAsync(System.IO.Path.Combine(executionContext.FunctionAppDirectory, "config", "templates.json")));
            var template = templates.First(a => a.Name == resource.TemplateName);
            resource.AppService = template.AppService.ToString();
            resource.SubscriptionId = template.Subscriptions.OrderBy(a=>Guid.NewGuid()).FirstOrDefault();
            resource.TenantId = resource.TenantId;
            var guid = Guid.NewGuid().ToString().Split('-')[1];
            resource.ResourceGroupName = "TRY-RG-"+ guid;
            resource.AppServiceName = "testappservicename-" + guid;
            resource.Region = Region.USWest.Name;
            resource.AzureEnvironment = AzureEnvironment.AzureGlobalCloud.Name;
            resource.DeploymentLoggingLevel = HttpLoggingDelegatingHandler.Level.BodyAndHeaders.ToString();
            resource.DeploymentCheckTries= 60;
            resource.DeploymentMode = DeploymentMode.Complete.ToString();
            resource.DeploymentCheckIntervalSeconds = 10;

            resource.ARMTemplateLink = "https://tro.blob.core.windows.net/armtemplates/"+ template.CsmTemplateFilePath + ".json";
            resource.ARMParameters = JsonConvert.SerializeObject( new Dictionary<string, Dictionary<string, object>>
            {
                { "msdeployPackageUrl", new Dictionary<string, object>{{"value", template.MSDeployPackageUrl} }},
                { "appServiceName", new Dictionary<string, object>{{"value", resource.AppServiceName } }}
            });
            return resource;
        }

        [FunctionName("DurableManageResources_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("CreateATrialResource", new TrialResource {TemplateName="Express" });

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

  
        [FunctionName("GlobalInstance")]
        public static async Task Monitor([TimerTrigger("*/30 * * * * *", RunOnStartup = true)] TimerInfo info, [OrchestrationClient] DurableOrchestrationClient starter, ILogger log)
        {
            const string instanceId = "GlobalInstance";

            //Check if an instance with the specified ID already exists.
            var existingInstance= await starter.GetStatusAsync(instanceId);
            log.LogInformation($"Orchestration_Loop with ID '{instanceId}' had status '{existingInstance.RuntimeStatus}' at {DateTime.UtcNow.ToString()}");

            //await starter.PurgeInstanceHistoryAsync(instanceId);
            //await starter.TerminateAsync(instanceId, "Old");
            if (existingInstance == null)
            {
                // An instance with the specified ID doesn't exist, create one.
                //await starter.StartNewAsync("Orchestration_Loop", instanceId, null);
                await starter.StartNewAsync("ManageResources", instanceId, new TrialResource { TemplateName = "Express" });

                log.LogInformation($"Started Orchestration_Loop with ID = '{instanceId}'.");
                await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(null, instanceId);
                log.LogInformation($"Completed Orchestration_Loop with ID = '{instanceId}'.");
            }
            else if (existingInstance.RuntimeStatus != OrchestrationRuntimeStatus.Running && existingInstance.RuntimeStatus != OrchestrationRuntimeStatus.ContinuedAsNew && existingInstance.RuntimeStatus != OrchestrationRuntimeStatus.Pending)
            {
                // An instance with the specified ID is at least scheduled, don't create a new one.
                await starter.TerminateAsync(instanceId, $"Terminating because existing RuntimeStatus is {existingInstance.RuntimeStatus}");
                log.LogInformation($"Orchestration_Loop with ID '{instanceId}' will be restarted. Previous status {existingInstance.RuntimeStatus}");
                await starter.StartNewAsync("ManageResources", instanceId, null);

            }
            else
            {
                log.LogInformation($"Orchestration_Loop with ID '{instanceId}' already exists. Sleeping");
            }

            return;
        }
    }

}