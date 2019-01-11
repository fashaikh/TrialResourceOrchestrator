using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Rest;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.Management.AppService.Fluent;

namespace TrialResourceOrchestrator.FunctionApp
{
    public static class CreateResource
    {
        [FunctionName("CreateResource")]
        public static async Task Run([QueueTrigger("create-items")] TrialResource myQueueItem,  ILogger log)
        {
            log.LogInformation("CreateResource QueueTrigger function processed a request.");
            var subscriptionId = myQueueItem.SubscriptionId;;
            AzureServiceTokenProvider azureServiceTokenProvider = new AzureServiceTokenProvider();
            var tenantId = myQueueItem.TenantId; 
            var tokenCredentials = new TokenCredentials(await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com/").ConfigureAwait(false));
            var azureCredentials = new AzureCredentials(
                tokenCredentials,
                tokenCredentials,
                tenantId,
                AzureEnvironment.FromName(myQueueItem.AzureEnvironment));
            var client = RestClient
                .Configure()
                .WithEnvironment(AzureEnvironment.FromName(myQueueItem.AzureEnvironment))
                .WithLogLevel(myQueueItem.DeploymentLoggingLevel.ToEnum<HttpLoggingDelegatingHandler.Level>())
                .WithCredentials(azureCredentials)
                .Build();
            var azure = Azure
                .Authenticate(client, tenantId)
                .WithSubscription(subscriptionId);
            //   var resourceManagementClient = new ResourceManagementClient(client);
            string rgName = myQueueItem.ResourceGroupName; //SdkContext.RandomResourceName(myQueueItem.ResourceGroupName, 24);
            string deploymentName = myQueueItem.AppServiceName;

            try
            {
                //var templateJson = File.ReadAllText(System.IO.Path.Combine(context.FunctionDirectory, "..\\FreeFunctionARMTemplate.json"));

                //=============================================================
                // Create resource group.

                Console.WriteLine("Creating a resource group with name: " + rgName);

                await azure.ResourceGroups.Define(rgName)
                        .WithRegion(myQueueItem.Region.ToEnum<Region>())
                        .CreateAsync();

                Console.WriteLine("Created a resource group with name: " + rgName);

                //=============================================================
                // Create a deployment for an Azure App Service via an ARM template.

                Console.WriteLine("Starting a deployment for an Azure App Service: " + deploymentName);

                await azure.Deployments.Define(deploymentName)
                        .WithExistingResourceGroup(rgName)
                        .WithTemplateLink("",null)
                        //.WithParameters(new Dictionary<string, Dictionary<string, object>>{
                        //        { "hostingPlanName", new Dictionary<string, object>{{"value",deploymentName}}},
                        //        { "skuName", new Dictionary<string, object>{{"value", "B1" }}},
                        //        { "skuCapacity", new Dictionary<string, object>{{"value",1}}},
                        //        { "webSiteName", new Dictionary<string, object>{{"value", deploymentName } }}
                        //    })
                        //.WithParameters(new Dictionary<string, Dictionary<string, object>>{
                        //       { "msdeployPackageUrl", new Dictionary<string, object>{{"value", "https://tryappservicetemplates.blob.core.windows.net/zipped/Default/Express.zip" } }},
                        //       { "appServiceName", new Dictionary<string, object>{{"value", deploymentName } }}
                        //   })
                        .WithParametersLink("", null)
                        .WithMode(myQueueItem.DeploymentMode.ToEnum<DeploymentMode>())
                        .CreateAsync();

                Console.WriteLine("Started a deployment for an Azure App Service: " + deploymentName);

                var deployment = await azure.Deployments.GetByResourceGroupAsync(rgName, deploymentName);
                Console.WriteLine("Current deployment status : " + deployment.ProvisioningState);

                var tries = 180;
                while (!(StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Succeeded") ||
                        StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Failed") ||
                        StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Cancelled")) && tries-- > 0)
                {
                    SdkContext.DelayProvider.Delay(10000);
                    deployment = await azure.Deployments.GetByResourceGroupAsync(rgName, deploymentName);
                    Console.WriteLine("Current deployment status : " + deployment.ProvisioningState);
                }
                if (StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Succeeded"))
                {
                    var res = deployment.Outputs;
                    var sitesDeployed = deployment.Dependencies.Where(a => StringComparer.OrdinalIgnoreCase.Equals(a.ResourceType, "Microsoft.Web/sites"));
                    if (sitesDeployed != null)
                    {
                        var siteList = new List<IWebApp>();
                        foreach (var site in sitesDeployed)
                        {
                            siteList.Add(await azure.WebApps.GetByIdAsync(site.Id));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                try
                {
                    Console.WriteLine("Deleting Resource Group: " + rgName);
                    azure.ResourceGroups.DeleteByName(rgName);
                    Console.WriteLine("Deleted Resource Group: " + rgName);
                }
                catch (NullReferenceException)
                {
                    Console.WriteLine("Did not create any resources in Azure. No clean up is necessary");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }
            string name = myQueueItem.AppServiceName;

        }
    }
}
