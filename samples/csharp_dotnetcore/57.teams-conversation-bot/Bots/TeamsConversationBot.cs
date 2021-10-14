// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using AdaptiveCards.Templating;
using System.Text.Json;
using Teams.Conversation.Bot;
using AdaptiveCards;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Microsoft.BotBuilderSamples.Bots
{
    public class TeamsConversationBot : TeamsActivityHandler
    {
        private string _appId;
        private static string _appPassword;
        private IDictionary<int, MessageModel> remindersStore = new Dictionary<int, MessageModel>();
        private IDictionary<string, string> remindersValues = new Dictionary<string, string>();
        int reminderId = 0;
        private static MessageModel previousmessage = null;
        public readonly string baseUrl;
        public readonly string clientID;
        public readonly string clientSecret;
        public readonly string graphAPIToken;


        public TeamsConversationBot(IConfiguration config)
        {
            _appId = config["MicrosoftAppId"];
            _appPassword = config["MicrosoftAppPassword"];
            this.baseUrl = config["BaseUrl"];
            this.clientID = config["MicrosoftAppId"];
            this.clientSecret = config["MicrosoftAppPassword"];
            this.graphAPIToken = config["GraphAPIToken"];

            reminderId++;
            remindersStore.Add(reminderId, new MessageModel
            {
                text = "Please review the design document",
                date = "20-10-2021 10:30:52 AM"
            });
            reminderId++;
            remindersStore.Add(reminderId, new MessageModel
            {
                text = "Please review the PR",
                date = "20-11-2021"
            });

            remindersValues.Add("1", "5 seconds");
            remindersValues.Add("2", "1 hours");
            remindersValues.Add("3", "3 hours");

        }
            

        private readonly string _adaptiveCardTemplate = Path.Combine(".", "Resources", "UserMentionCardTemplate.json");

        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            turnContext.Activity.RemoveRecipientMention();
            var conversationId = turnContext.Activity.Conversation.Id;
            var tenantId = turnContext.Activity.Conversation.TenantId;
            var infoText = "Please specify in this format: scheduleMessageLater AliasName [Message] [DateTime]";

            if (turnContext.Activity.Text != null)
            {
                var text = turnContext.Activity.Text.Trim().ToLower();

                /*                if (text.Contains("SaveToOneNote"))
                                {
                                    System.Console.WriteLine("############Calling SaveToOneNote");
                                    var text2 = turnContext.Activity.Text.Trim().ToLower();
                                    await SendMessageToOneNoteAsync(text2);
                                }
                                else */
                if (text.Equals("scheduleMessageLater --help"))
                    await turnContext.SendActivityAsync(MessageFactory.Text(infoText, infoText), cancellationToken);

                else if (text.Contains("scheduleMessageLater"))
                    await ScheduleMessageLater(turnContext, cancellationToken, text);
                else if (text.Contains("remindmelater"))
                    await SendReminderSetMessage(turnContext, cancellationToken);
                else if (text.Contains("listreminders"))
                    await ListAllReminders(turnContext, cancellationToken);
                else if (text.Contains("update"))
                    await CardActivityAsync(turnContext, true, cancellationToken);
                else if (text.Contains("message"))
                    await MessageAllMembersAsync(turnContext, cancellationToken);
                else if (text.Contains("delete"))
                    await DeleteCardActivityAsync(turnContext, cancellationToken);
                else
                    await CardActivityAsync(turnContext, false, cancellationToken);
            }
            // Special case : Here, when we create reminder using extension and press enter, we will reach here... just send the card back for previous message
            else
            {
                var activity = (Activity)GetCardForNewReminder(previousmessage.text);
                await turnContext.SendActivityAsync(activity, cancellationToken);
            }
        }


        private async Task ScheduleMessageLater(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken, string text)
        {
            string alias = text.Split(" ")[1].ToLower();
            string message = text.Split('[')[1].Split(']')[0];
            string dateTimeString = text.Split('[')[2].Split(']')[0];

            //var replyText = $"**Message:** {message} {Environment.NewLine} **Scheduled at:** {dateTimeString} {Environment.NewLine} **To:** {alias}";
            //await turnContext.SendActivityAsync(MessageFactory.Text(replyText, replyText), cancellationToken);
            var responseCard = GetCardForResponse(message, alias, dateTimeString);
            await turnContext.SendActivityAsync(responseCard, cancellationToken);

            try
            {
                var chatID = GetChatIDFromAliasManual(alias);
                Thread.Sleep(Int32.Parse(dateTimeString) * 1000);
                dynamic response = SendMessageWithChatID(message, chatID);
                Console.WriteLine(response.body.content);
                await turnContext.SendActivityAsync(MessageFactory.Text($"Message to {alias} has been sent!"), cancellationToken);
            }
            catch
            {
                await turnContext.SendActivityAsync(MessageFactory.Text($"Failed to send the message to {alias}."), cancellationToken);
            }

            //await turnContext.SendActivityAsync(MessageFactory.Text(chatID), cancellationToken);


        }

        private object GetObjectFromGraphAPI(string url)
        {
            dynamic response = null;
            try
            {
                var webRequest = System.Net.WebRequest.Create(url);
                if (webRequest != null)
                {
                    webRequest.Method = "GET";
                    webRequest.Timeout = 12000;
                    webRequest.ContentType = "application/json";
                    //var token = "eyJ0eXAiOiJKV1QiLCJub25jZSI6InV2empTbzUzeG8ycUlxV3VGWWVheTNTVThDaExDVkE4MGNDMi14NGk5YTQiLCJhbGciOiJSUzI1NiIsIng1dCI6Imwzc1EtNTBjQ0g0eEJWWkxIVEd3blNSNzY4MCIsImtpZCI6Imwzc1EtNTBjQ0g0eEJWWkxIVEd3blNSNzY4MCJ9.eyJhdWQiOiIwMDAwMDAwMy0wMDAwLTAwMDAtYzAwMC0wMDAwMDAwMDAwMDAiLCJpc3MiOiJodHRwczovL3N0cy53aW5kb3dzLm5ldC8wNDhhZDQ3OC05ZGNlLTQxYzctYWFiZi04ZTJmNmE4ZGU1MDIvIiwiaWF0IjoxNjM0MTQyNTU5LCJuYmYiOjE2MzQxNDI1NTksImV4cCI6MTYzNDE0NjQ1OSwiYWNjdCI6MCwiYWNyIjoiMSIsImFpbyI6IkUyWmdZTmpSOHE2MThaclUrZ1p4bFlhWFhkMDZYZHYyWHplNm85UHZsTnJ2c2RUNWhoc0EiLCJhbXIiOlsicHdkIl0sImFwcF9kaXNwbGF5bmFtZSI6IkdyYXBoIEV4cGxvcmVyIiwiYXBwaWQiOiJkZThiYzhiNS1kOWY5LTQ4YjEtYThhZC1iNzQ4ZGE3MjUwNjQiLCJhcHBpZGFjciI6IjAiLCJmYW1pbHlfbmFtZSI6IksiLCJnaXZlbl9uYW1lIjoiUHJlZXRoYW0iLCJpZHR5cCI6InVzZXIiLCJpcGFkZHIiOiI1OS45Mi4xMzguNjMiLCJuYW1lIjoiUHJlZXRoYW0gSyIsIm9pZCI6IjY3MWUxMDdhLWUxMDctNDliNy05MmJjLTc0Zjk5NTIyMDIzYyIsInBsYXRmIjoiMyIsInB1aWQiOiIxMDAzMjAwMTk1MzgwOTAyIiwicmgiOiIwLkFYQUFlTlNLQk02ZHgwR3F2NDR2YW8zbEFyWElpOTc1MmJGSXFLMjNTTnB5VUdSd0FNOC4iLCJzY3AiOiJBcHBDYXRhbG9nLlJlYWQuQWxsIEFwcENhdGFsb2cuUmVhZFdyaXRlLkFsbCBBcHBDYXRhbG9nLlN1Ym1pdCBDaGF0LlJlYWQgQ2hhdC5SZWFkQmFzaWMgQ2hhdC5SZWFkV3JpdGUgQ2hhdE1lbWJlci5SZWFkIENoYXRNZW1iZXIuUmVhZFdyaXRlIERpcmVjdG9yeS5SZWFkLkFsbCBEaXJlY3RvcnkuUmVhZFdyaXRlLkFsbCBNYWlsLlJlYWRCYXNpYyBOb3Rlcy5DcmVhdGUgTm90ZXMuUmVhZCBOb3Rlcy5SZWFkV3JpdGUgTm90ZXMuUmVhZFdyaXRlLkFsbCBvcGVuaWQgcHJvZmlsZSBUZWFtc0FwcEluc3RhbGxhdGlvbi5SZWFkRm9yVXNlciBUZWFtc0FwcEluc3RhbGxhdGlvbi5SZWFkV3JpdGVGb3JVc2VyIFRlYW1zQXBwSW5zdGFsbGF0aW9uLlJlYWRXcml0ZVNlbGZGb3JVc2VyIFVzZXIuUmVhZCBlbWFpbCIsInNpZ25pbl9zdGF0ZSI6WyJrbXNpIl0sInN1YiI6IlU1RDFMa1ZvMnVHaUFmV2ZjTGExaGk4d2ZnOEoyYUpqV1JCZEM2S21QVGciLCJ0ZW5hbnRfcmVnaW9uX3Njb3BlIjoiQVMiLCJ0aWQiOiIwNDhhZDQ3OC05ZGNlLTQxYzctYWFiZi04ZTJmNmE4ZGU1MDIiLCJ1bmlxdWVfbmFtZSI6InByZWV0aGFta0Bnb3lhbGRlbW8ub25taWNyb3NvZnQuY29tIiwidXBuIjoicHJlZXRoYW1rQGdveWFsZGVtby5vbm1pY3Jvc29mdC5jb20iLCJ1dGkiOiJiYXpZdEcyTzBVbXQycXFmSU5SQkFRIiwidmVyIjoiMS4wIiwid2lkcyI6WyIyOTIzMmNkZi05MzIzLTQyZmQtYWRlMi0xZDA5N2FmM2U0ZGUiLCI2MmU5MDM5NC02OWY1LTQyMzctOTE5MC0wMTIxNzcxNDVlMTAiLCJmMDIzZmQ4MS1hNjM3LTRiNTYtOTVmZC03OTFhYzAyMjYwMzMiLCI3Mjk4MjdlMy05YzE0LTQ5ZjctYmIxYi05NjA4ZjE1NmJiYjgiLCJmMjhhMWY1MC1mNmU3LTQ1NzEtODE4Yi02YTEyZjJhZjZiNmMiLCI2OTA5MTI0Ni0yMGU4LTRhNTYtYWE0ZC0wNjYwNzViMmE3YTgiLCJmZTkzMGJlNy01ZTYyLTQ3ZGItOTFhZi05OGMzYTQ5YTM4YjEiLCJmMmVmOTkyYy0zYWZiLTQ2YjktYjdjZi1hMTI2ZWU3NGM0NTEiLCJiNzlmYmY0ZC0zZWY5LTQ2ODktODE0My03NmIxOTRlODU1MDkiXSwieG1zX3N0Ijp7InN1YiI6InRoRjl5aGlOeGh5a1dhWVpqWjgtVXMwSGtqTG80RjlnSlhRNVFxeElLNEkifSwieG1zX3RjZHQiOjE2MzM1MjYxMDJ9.gs0lVSwzwvY15lzXRu6NAkiXhQdUZKMR-S-iNn-eV3aH8AG_wZEpz3O5sBtC3h4Jh0GXBkhonzpJjtm6tTGfRJTi2P_azZEtJcV8a6tQ1m9xnzNsO4z74oOnG9kZvtbx6BQj8QSbIrka19Vx2HDqb5q64ZiyKR58Cp5vr8UOTE6WBPSr23XpLGkFcMlbIExdvtIEDDJsOJUWf9WoVfAkTPvxD_aF32DSBLPuUCylNyeMJV28AgXo42lJTyZlRTVeG_XbBcwFth0L-C8uvr9XLcEotM_XUegogspjx4P2fxRM6v8TzCzPd1Ul1JlQBuCu2haeDDr5P90RKds3jIxMzg";
                    webRequest.Headers.Add("Authorization", $"Bearer {this.graphAPIToken}");

                    using (System.IO.Stream s = webRequest.GetResponse().GetResponseStream())
                    {
                        using (System.IO.StreamReader sr = new System.IO.StreamReader(s))
                        {
                            var jsonResponse = sr.ReadToEnd();
                            response = JsonConvert.DeserializeObject(jsonResponse);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return response;
        }

        private string GetChatIDFromAliasManual(string alias)
        {
            string WEBSERVICE_URL = "https://graph.microsoft.com/beta/me/chats/";
            dynamic response = GetObjectFromGraphAPI(WEBSERVICE_URL);
            string chatID = null;
            //Console.WriteLine(String.Format("Response: {0}", response));
            //Console.WriteLine(response["@odata.count"]);
            foreach (var chat in response.value)
            {
                if (chat.chatType == "oneOnOne")
                {
                    string url = $"https://graph.microsoft.com/beta/chats/{chat.id}/members";
                    dynamic chatInfo = GetObjectFromGraphAPI(url);
                    if (Int32.Parse(chatInfo["@odata.count"].ToString()) == 2 && chatInfo.value[1].email.ToString().Split("@")[0].ToLower() == alias)
                    {
                        //Console.WriteLine(chat.id);
                        chatID = chat.id.ToString();
                        break;
                    }
                }
            }

            return chatID;
        }

        private object SendMessageWithChatID(string message, string chatID)
        {
            dynamic response = null;
            try
            {
                var webRequest = System.Net.WebRequest.Create($"https://graph.microsoft.com/v1.0/chats/{chatID}/messages");
                if (webRequest != null)
                {
                    webRequest.Method = "POST";
                    webRequest.Timeout = 12000;
                    webRequest.ContentType = "application/json";
                    //var token = "eyJ0eXAiOiJKV1QiLCJub25jZSI6Il93WXJ3SE9VdG4tcEtHVEJqaTZCT2ZaZ05UTFZsNjAyOU9FMl9DaHlVNHciLCJhbGciOiJSUzI1NiIsIng1dCI6Imwzc1EtNTBjQ0g0eEJWWkxIVEd3blNSNzY4MCIsImtpZCI6Imwzc1EtNTBjQ0g0eEJWWkxIVEd3blNSNzY4MCJ9.eyJhdWQiOiIwMDAwMDAwMy0wMDAwLTAwMDAtYzAwMC0wMDAwMDAwMDAwMDAiLCJpc3MiOiJodHRwczovL3N0cy53aW5kb3dzLm5ldC8wNDhhZDQ3OC05ZGNlLTQxYzctYWFiZi04ZTJmNmE4ZGU1MDIvIiwiaWF0IjoxNjM0MTg4NDY3LCJuYmYiOjE2MzQxODg0NjcsImV4cCI6MTYzNDE5MjM2NywiYWNjdCI6MCwiYWNyIjoiMSIsImFpbyI6IkUyWmdZRkNldTBiRTZTTEh6aGJOZThkN2psclA4WE9Xcy9sK2I4ckZub1RWYVFadEZqVUEiLCJhbXIiOlsicHdkIl0sImFwcF9kaXNwbGF5bmFtZSI6IkdyYXBoIEV4cGxvcmVyIiwiYXBwaWQiOiJkZThiYzhiNS1kOWY5LTQ4YjEtYThhZC1iNzQ4ZGE3MjUwNjQiLCJhcHBpZGFjciI6IjAiLCJmYW1pbHlfbmFtZSI6IksiLCJnaXZlbl9uYW1lIjoiUHJlZXRoYW0iLCJpZHR5cCI6InVzZXIiLCJpcGFkZHIiOiI1OS45My4yMjYuMjU1IiwibmFtZSI6IlByZWV0aGFtIEsiLCJvaWQiOiI2NzFlMTA3YS1lMTA3LTQ5YjctOTJiYy03NGY5OTUyMjAyM2MiLCJwbGF0ZiI6IjMiLCJwdWlkIjoiMTAwMzIwMDE5NTM4MDkwMiIsInJoIjoiMC5BWEFBZU5TS0JNNmR4MEdxdjQ0dmFvM2xBclhJaTk3NTJiRklxSzIzU05weVVHUndBTTguIiwic2NwIjoiQXBwQ2F0YWxvZy5SZWFkLkFsbCBBcHBDYXRhbG9nLlJlYWRXcml0ZS5BbGwgQXBwQ2F0YWxvZy5TdWJtaXQgQ2hhdC5SZWFkIENoYXQuUmVhZEJhc2ljIENoYXQuUmVhZFdyaXRlIENoYXRNZW1iZXIuUmVhZCBDaGF0TWVtYmVyLlJlYWRXcml0ZSBEaXJlY3RvcnkuUmVhZC5BbGwgRGlyZWN0b3J5LlJlYWRXcml0ZS5BbGwgTWFpbC5SZWFkQmFzaWMgTm90ZXMuQ3JlYXRlIE5vdGVzLlJlYWQgTm90ZXMuUmVhZFdyaXRlIE5vdGVzLlJlYWRXcml0ZS5BbGwgb3BlbmlkIHByb2ZpbGUgVGVhbXNBcHBJbnN0YWxsYXRpb24uUmVhZEZvclVzZXIgVGVhbXNBcHBJbnN0YWxsYXRpb24uUmVhZFdyaXRlRm9yVXNlciBUZWFtc0FwcEluc3RhbGxhdGlvbi5SZWFkV3JpdGVTZWxmRm9yVXNlciBVc2VyLlJlYWQgZW1haWwiLCJzaWduaW5fc3RhdGUiOlsia21zaSJdLCJzdWIiOiJVNUQxTGtWbzJ1R2lBZldmY0xhMWhpOHdmZzhKMmFKaldSQmRDNkttUFRnIiwidGVuYW50X3JlZ2lvbl9zY29wZSI6IkFTIiwidGlkIjoiMDQ4YWQ0NzgtOWRjZS00MWM3LWFhYmYtOGUyZjZhOGRlNTAyIiwidW5pcXVlX25hbWUiOiJwcmVldGhhbWtAZ295YWxkZW1vLm9ubWljcm9zb2Z0LmNvbSIsInVwbiI6InByZWV0aGFta0Bnb3lhbGRlbW8ub25taWNyb3NvZnQuY29tIiwidXRpIjoiR0FlN1NmSUYxVWFIcFE2Ym1NRVZBQSIsInZlciI6IjEuMCIsIndpZHMiOlsiMjkyMzJjZGYtOTMyMy00MmZkLWFkZTItMWQwOTdhZjNlNGRlIiwiNjJlOTAzOTQtNjlmNS00MjM3LTkxOTAtMDEyMTc3MTQ1ZTEwIiwiZjAyM2ZkODEtYTYzNy00YjU2LTk1ZmQtNzkxYWMwMjI2MDMzIiwiNzI5ODI3ZTMtOWMxNC00OWY3LWJiMWItOTYwOGYxNTZiYmI4IiwiZjI4YTFmNTAtZjZlNy00NTcxLTgxOGItNmExMmYyYWY2YjZjIiwiNjkwOTEyNDYtMjBlOC00YTU2LWFhNGQtMDY2MDc1YjJhN2E4IiwiZmU5MzBiZTctNWU2Mi00N2RiLTkxYWYtOThjM2E0OWEzOGIxIiwiZjJlZjk5MmMtM2FmYi00NmI5LWI3Y2YtYTEyNmVlNzRjNDUxIiwiYjc5ZmJmNGQtM2VmOS00Njg5LTgxNDMtNzZiMTk0ZTg1NTA5Il0sInhtc19zdCI6eyJzdWIiOiJ0aEY5eWhpTnhoeWtXYVlaalo4LVVzMEhrakxvNEY5Z0pYUTVRcXhJSzRJIn0sInhtc190Y2R0IjoxNjMzNTI2MTAyfQ.HmHWviLQNYZHfQFnD4lKoA_IgRoTnKMR0-BuFqlEKsweaVbqNzbgd96BXNFVVYlc9_lEKySfe0jAyn2w-iMRb7PftJWBH3onKp38mCwz9T_Qc2xXZeF6vM70gciIEyD3N8sLFzo-HK0Q7bPHvzFhWYBIMHpPwFqvGlIbFsWiGDIfUufU6kroJ-n56yslIcVqqxGYTV_sMO9JdcE7sBNzhpk-gYZoFhsQI0IxUuxiLzipN2AXEeYD50L0JuoksgacJbhUx49J1yEjrjwJgeRZORSCjO1K2XQ61AxJazIiNdJysFKwLCoH2hNOmE3m8kwC13oRrKf87A10QrX6hvyX-w";
                    webRequest.Headers.Add("Authorization", $"Bearer {this.graphAPIToken}");

                    string stringData = " {\"body\": {\"content\": \" " + message + " \"}  } ";
                    var data = Encoding.Default.GetBytes(stringData); // note: choose appropriate encoding
                    webRequest.ContentLength = data.Length;

                    var newStream = webRequest.GetRequestStream(); // get a ref to the request body so it can be modified
                    newStream.Write(data, 0, data.Length);
                    newStream.Close();

                    using (System.IO.Stream s = webRequest.GetResponse().GetResponseStream())
                    {
                        using (System.IO.StreamReader sr = new System.IO.StreamReader(s))
                        {
                            var jsonResponse = sr.ReadToEnd();
                            response = JsonConvert.DeserializeObject(jsonResponse);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return response;
        }


        private async Task SendMessageToOneNoteAsync(string text, string heading)
        {
            string token = "eyJ0eXAiOiJKV1QiLCJub25jZSI6Ijg1RzlENHJ1U1NJV0g1VTFMSG85TzRDcWY2Q05YQmxPQkJHUXE5R3gta0UiLCJhbGciOiJSUzI1NiIsIng1dCI6Imwzc1EtNTBjQ0g0eEJWWkxIVEd3blNSNzY4MCIsImtpZCI6Imwzc1EtNTBjQ0g0eEJWWkxIVEd3blNSNzY4MCJ9.eyJhdWQiOiIwMDAwMDAwMy0wMDAwLTAwMDAtYzAwMC0wMDAwMDAwMDAwMDAiLCJpc3MiOiJodHRwczovL3N0cy53aW5kb3dzLm5ldC8wNDhhZDQ3OC05ZGNlLTQxYzctYWFiZi04ZTJmNmE4ZGU1MDIvIiwiaWF0IjoxNjM0MTI5NjgxLCJuYmYiOjE2MzQxMjk2ODEsImV4cCI6MTYzNDEzMzU4MSwiYWNjdCI6MCwiYWNyIjoiMSIsImFpbyI6IkFTUUEyLzhUQUFBQXpLazlNRFV3cFowRzNGQ0RUVXFBUHNwd3FZWEIwWTUydHJBa3hNYlpaNkU9IiwiYW1yIjpbInB3ZCJdLCJhcHBfZGlzcGxheW5hbWUiOiJHcmFwaCBFeHBsb3JlciIsImFwcGlkIjoiZGU4YmM4YjUtZDlmOS00OGIxLWE4YWQtYjc0OGRhNzI1MDY0IiwiYXBwaWRhY3IiOiIwIiwiZmFtaWx5X25hbWUiOiJnb3lhbCIsImdpdmVuX25hbWUiOiJhc2VlbSIsImlkdHlwIjoidXNlciIsImlwYWRkciI6IjQ5LjM2LjE4OC4xNDQiLCJuYW1lIjoiYXNlZW0gZ295YWwiLCJvaWQiOiJkYmRmZDQxZC05YThmLTRjMWYtYWMzMS1kYTM2Y2ZlNzZlYzkiLCJwbGF0ZiI6IjMiLCJwdWlkIjoiMTAwMzIwMDE5MkZFQzAzOSIsInJoIjoiMC5BWEFBZU5TS0JNNmR4MEdxdjQ0dmFvM2xBclhJaTk3NTJiRklxSzIzU05weVVHUndBQ2cuIiwic2NwIjoiQXBwQ2F0YWxvZy5SZWFkLkFsbCBBcHBDYXRhbG9nLlJlYWRXcml0ZS5BbGwgQXBwQ2F0YWxvZy5TdWJtaXQgQ2hhdC5SZWFkIENoYXQuUmVhZFdyaXRlIENoYXRNZW1iZXIuUmVhZCBDaGF0TWVtYmVyLlJlYWRXcml0ZSBEaXJlY3RvcnkuUmVhZC5BbGwgRGlyZWN0b3J5LlJlYWRXcml0ZS5BbGwgTWFpbC5SZWFkQmFzaWMgTm90ZXMuQ3JlYXRlIE5vdGVzLlJlYWQgTm90ZXMuUmVhZFdyaXRlIE5vdGVzLlJlYWRXcml0ZS5BbGwgb3BlbmlkIHByb2ZpbGUgVGVhbXNBcHBJbnN0YWxsYXRpb24uUmVhZEZvclVzZXIgVXNlci5SZWFkIGVtYWlsIiwic2lnbmluX3N0YXRlIjpbImttc2kiXSwic3ViIjoiQzJCSFh6LTNScHFRaGlPcHplNncydGpfUkI4TXpJdkNDUk12TlYySXBtdyIsInRlbmFudF9yZWdpb25fc2NvcGUiOiJBUyIsInRpZCI6IjA0OGFkNDc4LTlkY2UtNDFjNy1hYWJmLThlMmY2YThkZTUwMiIsInVuaXF1ZV9uYW1lIjoiYXNlZW1AZ295YWxkZW1vLm9ubWljcm9zb2Z0LmNvbSIsInVwbiI6ImFzZWVtQGdveWFsZGVtby5vbm1pY3Jvc29mdC5jb20iLCJ1dGkiOiJ0UGk5MEZXSkRVYTJsU3F5WVVpdUFRIiwidmVyIjoiMS4wIiwid2lkcyI6WyI2MmU5MDM5NC02OWY1LTQyMzctOTE5MC0wMTIxNzcxNDVlMTAiLCJiNzlmYmY0ZC0zZWY5LTQ2ODktODE0My03NmIxOTRlODU1MDkiXSwieG1zX3N0Ijp7InN1YiI6ImxSRXZfUDFPR1l5WGlIbEVSSTdZemtfQ1NIUEtvREVjZWZQS2wxUVBramsifSwieG1zX3RjZHQiOjE2MzM1MjYxMDJ9.kKP8rjPPrzEW6GYeBxY6HtZKywP8DLDMK29Gvmf6F9O5lt1webX0qQpRxVlmMWi9l8mCz7tq05q3k-A-zfSSPVmAnFTIjnSpRRP3b2C1gqUnPIYwiV81Bf60s-PxV0uKmxjMkNdDZ5BhFMk2NMiRYJLJUjiyx-aOgREmK_kn5HM9_bD8Op9rsKzF_b9L0tpWV8JnlcIB0yXMb2nKqAjejel4tVrJ5lb95ZpGhbTC3trOO0eOd_UthgStvBYD-vLrdJZEYXLa0uHVwAzx2pidS_m2eiD6iHBCYykxFC6V98GdlFrni9zk93wOZku3KV8-6WwdWhT2ZDQzPuBYlFsJ6Q";
            using (var client = new HttpClient())
            {
                string inputMsg = text;//turnContext.Activity.Text.Trim().ToLower();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using (var content = new MultipartFormDataContent("MyPartBoundary198374"))
                {
                    var stringContent = new StringContent("<html><head><title>" + heading + "</title></head>", Encoding.UTF8, "text/html");
                    content.Add(stringContent, "<body>" + text + "</body></html>");
                    using (
                        var message =
                           await client.PostAsync("https://graph.microsoft.com/v1.0/me/onenote/sections/1-fc511081-61b5-4e46-b84d-3accb8ba4872/pages", content))
                    {
                        Console.WriteLine(message.StatusCode);
                    }
                }
            }

        }

        private async Task ListAllReminders(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var activity = new Activity();
            foreach (KeyValuePair<int, MessageModel> entry in remindersStore)
            {
                // do something with entry.Value or entry.Key
                activity = (Activity) GetCardForNewReminder(entry.Value.text.ToString());
               await turnContext.SendActivityAsync(activity, cancellationToken);
            }
        }

        protected override async Task<MessagingExtensionActionResponse> OnTeamsMessagingExtensionSubmitActionAsync(ITurnContext<IInvokeActivity> turnContext, MessagingExtensionAction action, CancellationToken cancellationToken)
        {
            switch (action.CommandId)
            {
                //case "createCard":
                //  return CreateCardCommand(turnContext, action);
                case "RemindMeLater":
                    return RemindMeLaterMessageExtension(turnContext, action);
                case "RemindMeLater2":
                    return RemindMeLaterMessageExtension(turnContext, action);
                case "SaveToOneDrive":
                    var sectionAndHeadingheading = ((JObject)action.Data)["Save"]?.ToString();
                    var groups = sectionAndHeadingheading.Split("/", 3);
                    var text2 = action.MessagePayload.Body.Content;
                    await SendMessageToOneNoteAsync(text2, groups[1]);
                    return new MessagingExtensionActionResponse();
            }
            return new MessagingExtensionActionResponse();
        }

        private MessagingExtensionActionResponse RemindMeLaterMessageExtension(ITurnContext<IInvokeActivity> turnContext, MessagingExtensionAction action)
        {
            // The user has chosen to share a message by choosing the 'Share Message' context menu command.


            //ReminderCreateModel model =
            //JsonSerializer.Deserialize<ReminderCreateModel>(JsonSerializer.Serialize(action.Data));

            var id = ((JObject)action.Data)["RemindAfter"]?.ToString();

            string timeSnooze = null;
            var time = remindersValues.TryGetValue(id, out timeSnooze);
            var messageLink = action.MessagePayload.LinkToMessage;
            var heroCard = new HeroCard
            {
                Title = $"The message is scheduled for later time: {timeSnooze}",
                Text = action.MessagePayload.Body.Content,
                Subtitle = messageLink.ToString()
            };

            previousmessage = new MessageModel
            {
                text = action.MessagePayload.Body.Content,
                link = messageLink,
                date = timeSnooze
            };

            reminderId++;
            remindersStore.Add(reminderId, previousmessage);

            var Buttons = new List<CardAction> { new CardAction(ActionTypes.OpenUrl, "View Original Message", value: messageLink) };
            heroCard.Buttons = Buttons;


            // This Messaging Extension example allows the user to check a box to include an image with the
            // shared message.  This demonstrates sending custom parameters along with the message payload.
            var includeImage = ((JObject)action.Data)["includeImage"]?.ToString();
            if (string.Equals(includeImage, bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                heroCard.Images = new List<CardImage>
                {
                    new CardImage { Url = "https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcQtB3AwMUeNoq4gUBGe6Ocj8kyh3bXa9ZbV7u1fVKQoyKFHdkqU" },
                };
            }

            SendReminderHack(action.MessagePayload.Body.Content.ToString(), turnContext, new CancellationToken());

            return new MessagingExtensionActionResponse
            {
                ComposeExtension = new MessagingExtensionResult
                {
                    Type = "result",
                    AttachmentLayout = "list",
                    Attachments = new List<MessagingExtensionAttachment>()
                    {
                        new MessagingExtensionAttachment
                        {
                            Content = heroCard,
                            ContentType = HeroCard.ContentType,
                            Preview = heroCard.ToAttachment(),
                        },
                    },
                },
            };
        }


        private async Task SendReminderSetMessage(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            // The user has chosen to share a message by choosing the 'Share Message' context menu command.


            //ReminderCreateModel model =
            //JsonSerializer.Deserialize<ReminderCreateModel>(JsonSerializer.Serialize(action.Data));
            string input = turnContext.Activity.Text.Trim().ToLower();
            string outputString = input.Replace("RemindMeLater ", "");
             outputString = outputString.Replace("remindmelater ", "");

            string pat = @"(\/[0-9])(.*)";

            // Instantiate the regular expression object.
            Regex r = new Regex(pat, RegexOptions.IgnoreCase);

            // Match the regular expression pattern against a text string.
            Match m = r.Match(outputString);
            string timeSnooze = m.Groups[1].Value.Replace("/", "");
            string message = m.Groups[2].Value;


            reminderId++;
            remindersStore.Add(reminderId, new MessageModel { text =  message });

            var heroCard = new HeroCard
            {
                Title = $"Reminder has been scheduled for {timeSnooze} seconds!",
                Text = message,
            };

            var activity = MessageFactory.Attachment(heroCard.ToAttachment());

            await turnContext.SendActivityAsync(activity, cancellationToken);

            Thread.Sleep(5000);
            await SendReminderHack(message, turnContext, cancellationToken);

        }

        private void SendReminderHack(string outputString, ITurnContext<IInvokeActivity> turnContext, CancellationToken cancellationToken)
        {
            var activity = GetCardForNewReminder(outputString);
            // Echo back what the user said
            turnContext.SendActivityAsync(activity, cancellationToken);
        }

        protected async Task SendReminderHack(string outputString, ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var activity = GetCardForNewReminder(outputString);
            // Echo back what the user said
            await turnContext.SendActivityAsync(activity, cancellationToken);
        }

        protected IMessageActivity GetCardForNewReminder(String message)
        {

            var card = new HeroCard();

            card.Title = "You asked me to remind!!";
            card.Text = message;

            card.Buttons = new List<CardAction>();


            card.Buttons.Add(new CardAction
            {
                Type = ActionTypes.MessageBack,
                Title = "Snooze",
                Text = "Snooze"
            });

            card.Buttons.Add(new CardAction
            {
                Type = ActionTypes.MessageBack,
                Title = "View Message",
                Text = "ViewMessage"
            });

            card.Buttons.Add(new CardAction
            {
                Type = ActionTypes.MessageBack,
                Title = "Done",
                Text = "Delete reminder"
            });

            var activity = MessageFactory.Attachment(card.ToAttachment());
            return activity;

        }


        protected override async Task OnTeamsMembersAddedAsync(IList<TeamsChannelAccount> membersAdded, TeamInfo teamInfo, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            foreach (var teamMember in membersAdded)
            {
                await turnContext.SendActivityAsync(MessageFactory.Text($"Welcome to the team {teamMember.GivenName} {teamMember.Surname}."), cancellationToken);
            }
        }

        private async Task CardActivityAsync(ITurnContext<IMessageActivity> turnContext, bool update, CancellationToken cancellationToken)
        {

            var card = new HeroCard
            {
                Buttons = new List<CardAction>
                        {
                            new CardAction
                            {
                                Type = ActionTypes.MessageBack,
                                Title = "Message all members",
                                Text = "MessageAllMembers"
                            },
                            new CardAction
                            {
                                Type = ActionTypes.MessageBack,
                                Title = "Who am I?",
                                Text = "whoami"
                            },
                            new CardAction
                            {
                                Type = ActionTypes.MessageBack,
                                Title = "Find me in Adaptive Card",
                                Text = "mention me"
                            },
                            new CardAction
                            {
                                Type = ActionTypes.MessageBack,
                                Title = "Delete card",
                                Text = "Delete"
                            }
                        }
            };


            if (update)
            {
                await SendUpdatedCard(turnContext, card, cancellationToken);
            }
            else
            {
                await SendWelcomeCard(turnContext, card, cancellationToken);
            }

        }

        private async Task GetSingleMemberAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var member = new TeamsChannelAccount();

            try
            {
                member = await TeamsInfo.GetMemberAsync(turnContext, turnContext.Activity.From.Id, cancellationToken);
            }
            catch (ErrorResponseException e)
            {
                if (e.Body.Error.Code.Equals("MemberNotFoundInConversation"))
                {
                    await turnContext.SendActivityAsync("Member not found.");
                    return;
                }
                else
                {
                    throw e;
                }
            }

            var message = MessageFactory.Text($"You are: {member.Name}.");
            var res = await turnContext.SendActivityAsync(message);

        }

        private async Task DeleteCardActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            await turnContext.DeleteActivityAsync(turnContext.Activity.ReplyToId, cancellationToken);
        }

        private async Task MessageAllMembersAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var teamsChannelId = turnContext.Activity.TeamsGetChannelId();
            var serviceUrl = turnContext.Activity.ServiceUrl;
            var credentials = new MicrosoftAppCredentials(_appId, _appPassword);
            ConversationReference conversationReference = null;

            var members = await GetPagedMembers(turnContext, cancellationToken);

            foreach (var teamMember in members)
            {
                var proactiveMessage = MessageFactory.Text($"Hello {teamMember.GivenName} {teamMember.Surname}. I'm a Teams conversation bot.");

                var conversationParameters = new ConversationParameters
                {
                    IsGroup = false,
                    Bot = turnContext.Activity.Recipient,
                    Members = new ChannelAccount[] { teamMember },
                    TenantId = turnContext.Activity.Conversation.TenantId,
                };

                await ((CloudAdapter)turnContext.Adapter).CreateConversationAsync(
                    credentials.MicrosoftAppId,
                    teamsChannelId,
                    serviceUrl,
                    credentials.OAuthScope,
                    conversationParameters,
                    async (t1, c1) =>
                    {
                        conversationReference = t1.Activity.GetConversationReference();
                        await ((CloudAdapter)turnContext.Adapter).ContinueConversationAsync(
                            _appId,
                            conversationReference,
                            async (t2, c2) =>
                            {
                                await t2.SendActivityAsync(proactiveMessage, c2);
                            },
                            cancellationToken);
                    },
                    cancellationToken);
            }

            await turnContext.SendActivityAsync(MessageFactory.Text("All messages have been sent."), cancellationToken);
        }

        private static async Task<List<TeamsChannelAccount>> GetPagedMembers(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var members = new List<TeamsChannelAccount>();
            string continuationToken = null;

            do
            {
                var currentPage = await TeamsInfo.GetPagedMembersAsync(turnContext, 100, continuationToken, cancellationToken);
                continuationToken = currentPage.ContinuationToken;
                members = members.Concat(currentPage.Members).ToList();
            }
            while (continuationToken != null);

            return members;
        }

        private static async Task SendWelcomeCard(ITurnContext<IMessageActivity> turnContext, HeroCard card, CancellationToken cancellationToken)
        {
            var initialValue = new JObject { { "count", 0 } };
            card.Title = "Welcome!";
            card.Buttons.Add(new CardAction
            {
                Type = ActionTypes.MessageBack,
                Title = "Update Card",
                Text = "UpdateCardAction",
                Value = initialValue
            });

            var activity = MessageFactory.Attachment(card.ToAttachment());

            await turnContext.SendActivityAsync(activity, cancellationToken);
        }

        private static async Task SendUpdatedCard(ITurnContext<IMessageActivity> turnContext, HeroCard card, CancellationToken cancellationToken)
        {
            card.Title = "I've been updated";

            var data = turnContext.Activity.Value as JObject;
            data = JObject.FromObject(data);
            data["count"] = data["count"].Value<int>() + 1;
            card.Text = $"Update count - {data["count"].Value<int>()}";

            card.Buttons.Add(new CardAction
            {
                Type = ActionTypes.MessageBack,
                Title = "Update Card",
                Text = "UpdateCardAction",
                Value = data
            });

            var activity = MessageFactory.Attachment(card.ToAttachment());
            activity.Id = turnContext.Activity.ReplyToId;

            await turnContext.UpdateActivityAsync(activity, cancellationToken);
        }
/*
        private async Task MentionAdaptiveCardActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var member = new TeamsChannelAccount();

            try
            {
                member = await TeamsInfo.GetMemberAsync(turnContext, turnContext.Activity.From.Id, cancellationToken);
            }
            catch (ErrorResponseException e)
            {
                if (e.Body.Error.Code.Equals("MemberNotFoundInConversation"))
                {
                    await turnContext.SendActivityAsync("Member not found.");
                    return;
                }
                else
                {
                    throw e;
                }
            }

            var templateJSON = File.ReadAllText(_adaptiveCardTemplate);
            AdaptiveCardTemplate template = new AdaptiveCardTemplate(templateJSON);
            var memberData = new
            {
                userName = member.Name,
                userUPN = member.UserPrincipalName,
                userAAD = member.AadObjectId
            };
            string cardJSON = template.Expand(memberData);
            var adaptiveCardAttachment = new Attachment
            {
                ContentType = "application/vnd.microsoft.card.adaptive",
                Content = JsonConvert.DeserializeObject(cardJSON),
            };
            await turnContext.SendActivityAsync(MessageFactory.Attachment(adaptiveCardAttachment), cancellationToken);
        }
*/
        private async Task MentionActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var mention = new Mention
            {
                Mentioned = turnContext.Activity.From,
                Text = $"<at>{XmlConvert.EncodeName(turnContext.Activity.From.Name)}</at>",
            };

            var replyActivity = MessageFactory.Text($"Hello {mention.Text}.");
            replyActivity.Entities = new List<Entity> { mention };

            await turnContext.SendActivityAsync(replyActivity, cancellationToken);
        }


        //-----Subscribe to Conversation Events in Bot integration
        protected override async Task OnTeamsChannelCreatedAsync(ChannelInfo channelInfo, TeamInfo teamInfo, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            var heroCard = new HeroCard(text: $"{channelInfo.Name} is the Channel created");
            await turnContext.SendActivityAsync(MessageFactory.Attachment(heroCard.ToAttachment()), cancellationToken);
        }

        protected override async Task OnTeamsChannelRenamedAsync(ChannelInfo channelInfo, TeamInfo teamInfo, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            var heroCard = new HeroCard(text: $"{channelInfo.Name} is the new Channel name");
            await turnContext.SendActivityAsync(MessageFactory.Attachment(heroCard.ToAttachment()), cancellationToken);
        }

        protected override async Task OnTeamsChannelDeletedAsync(ChannelInfo channelInfo, TeamInfo teamInfo, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            var heroCard = new HeroCard(text: $"{channelInfo.Name} is the Channel deleted");
            await turnContext.SendActivityAsync(MessageFactory.Attachment(heroCard.ToAttachment()), cancellationToken);
        }

        protected override async Task OnTeamsMembersRemovedAsync(IList<TeamsChannelAccount> membersRemoved, TeamInfo teamInfo, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            foreach (TeamsChannelAccount member in membersRemoved)
            {
                if (member.Id == turnContext.Activity.Recipient.Id)
                {
                    // The bot was removed
                    // You should clear any cached data you have for this team
                }
                else
                {
                    var heroCard = new HeroCard(text: $"{member.Name} was removed from {teamInfo.Name}");
                    await turnContext.SendActivityAsync(MessageFactory.Attachment(heroCard.ToAttachment()), cancellationToken);
                }
            }
        }

        protected override async Task OnTeamsTeamRenamedAsync(TeamInfo teamInfo, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            var heroCard = new HeroCard(text: $"{teamInfo.Name} is the new Team name");
            await turnContext.SendActivityAsync(MessageFactory.Attachment(heroCard.ToAttachment()), cancellationToken);
        }
        protected override async Task OnReactionsAddedAsync(IList<MessageReaction> messageReactions, ITurnContext<IMessageReactionActivity> turnContext, CancellationToken cancellationToken)
        {
            foreach (var reaction in messageReactions)
            {
                var newReaction = $"You reacted with '{reaction.Type}' to the following message: '{turnContext.Activity.ReplyToId}'";
                var replyActivity = MessageFactory.Text(newReaction);
                await turnContext.SendActivityAsync(replyActivity, cancellationToken);
            }
        }

        protected override async Task OnReactionsRemovedAsync(IList<MessageReaction> messageReactions, ITurnContext<IMessageReactionActivity> turnContext, CancellationToken cancellationToken)
        {
            foreach (var reaction in messageReactions)
            {
                var newReaction = $"You removed the reaction '{reaction.Type}' from the following message: '{turnContext.Activity.ReplyToId}'";
                var replyActivity = MessageFactory.Text(newReaction);
                await turnContext.SendActivityAsync(replyActivity, cancellationToken);
            }
        }


        private async Task SendScheduledMessageResponse(ITurnContext<IInvokeActivity> turnContext, CancellationToken cancellationToken, string replyText)
        {
            //var activity = GetCardForNewReminder(outputString);
            // Echo back what the user said
            await turnContext.SendActivityAsync(MessageFactory.Text(replyText), cancellationToken);
        }


        private async Task<string> GetChatIDFromAlias(string recipient)
        {
            var client = GetGraphClient();
            var chats = await client.Me.Chats.Request().GetAsync();

            //foreach (var chat in chats)
            //{
            //    var chatID = chat.Id;

            //    var members = await client.Chats[chatID].Members
            //                        .Request()
            //                        .GetAsync();


            //}
            return chats.ToString();
        }

        protected IMessageActivity GetCardForResponse(String message, string alias, string dateTimeString)
        {

            var card = new HeroCard();

            card.Title = "Scheduled Message Details";
            card.Subtitle = $"To: {alias}, After: {dateTimeString} seconds";
            card.Text = $"Message: {message}";

            var activity = MessageFactory.Attachment(card.ToAttachment());
            return activity;

        }
    }
}
