using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using NLog.Extensions.Logging;
using Zeebe.Client;
using Zeebe.Client.Api.Responses;
using Zeebe.Client.Api.Worker;

namespace CsharpZeebeWorker
{
    class Program
    {

        private static readonly string DemoProcessPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "orchestrate-microservice.bpmn");
        private static readonly string ZeebeUrl = "0.0.0.0:26500";
        private static readonly string ProcessInstanceVariables = "{\"a\":\"123\"}";
        private static readonly string JobType = "restcall";
        private static readonly string WorkerName = Environment.MachineName;
        private static readonly long WorkCount = 100L;

        static async Task Main(string[] args)
        {

            // create zeebe client
            var client = ZeebeClient.Builder()
                .UseLoggerFactory(new NLogLoggerFactory())
                .UseGatewayAddress(ZeebeUrl)
                .UsePlainText()
                .Build();

            //fetch zeebe topology
            var topology = await client.TopologyRequest()
                .Send();
            Console.WriteLine(topology);

            // deploy
            var deployResponse = await client.NewDeployCommand()
                .AddResourceFile(DemoProcessPath)
                .Send();

            // create process instance
            var processDefinitionKey = deployResponse.Processes[0].ProcessDefinitionKey;

            var processInstance = await client
                .NewCreateProcessInstanceCommand()
                .ProcessDefinitionKey(processDefinitionKey)
                .Variables(ProcessInstanceVariables)
                .Send();

            // open job worker
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                client.NewWorker()
                      .JobType(JobType)
                      .Handler(HandleJobAsync)
                      .MaxJobsActive(5)
                      .Name(WorkerName)
                      .AutoCompletion()
                      .PollInterval(TimeSpan.FromSeconds(1))
                      .Timeout(TimeSpan.FromSeconds(10))
                      .Open();

                // blocks main thread, so that worker can run
                signal.WaitOne();
            }
        }

        private static async Task HandleJobAsync(IJobClient jobClient, IJob job)
        {
            // business logic
            Console.WriteLine("Handling job: " + job);

            var client = new HttpClient();

            var response = await client.GetAsync("http://cat-fact.herokuapp.com/facts");

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadAsStringAsync();
            var jsonString = Newtonsoft.Json.JsonConvert.DeserializeObject<List<CatFact>>(result);
            

            if (!response.IsSuccessStatusCode)            
            {
                jobClient.NewFailCommand(job.Key)
                    .Retries(job.Retries - 1)
                    .ErrorMessage("Example fail")
                    .Send()
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                jobClient.NewCompleteJobCommand(job.Key)
                    .Variables("{\"catfacts\":"+result+"}")
                    .Send()
                    .GetAwaiter()
                    .GetResult();
            }
        }
    }

    class CatFact {
        public string text { get; set; }
    }

    class CatFacts
    {
        public CatFact facts { get; set; }
    }
}

