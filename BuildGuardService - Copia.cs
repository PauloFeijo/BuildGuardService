using System;
using System.Diagnostics;
using System.Net.Http;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ini.Net;

namespace BuildGuardService
{
    public partial class BuildGuardService : ServiceBase
    {
        private static string hostArduino;

        public BuildGuardService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            ThreadStart start = new ThreadStart(ConsultarAPIJenkinsEnviarArduino);
            Thread thread = new Thread(start);
            thread.Start();
        }

        async protected override void OnStop()
        {
            string result = await EnviarArduinoInicioFim('p');
            EventLog.WriteEntry("Envio de parada ao arduino: " + result, EventLogEntryType.Information);
        }

        async public void ConsultarAPIJenkinsEnviarArduino()
        {                       
            const string SUCESSO = "SUCCESS";
            const string FALHA = "FAILURE";

            string path = System.AppDomain.CurrentDomain.BaseDirectory.ToString();

            IniFile ini = new IniFile(path + "\\Config.ini");            
            string serverJenkins = ini.ReadString("CONFIG", "Jenkins");
            hostArduino = ini.ReadString("CONFIG", "Arduino");
            int tempo = ini.ReadInteger("CONFIG", "Temposegundos") * 1000;
            string[] projetos = System.IO.File.ReadAllLines(path+"\\Projetos.ini");

            string result = await EnviarArduinoInicioFim('p');
            EventLog.WriteEntry("Envio de início ao arduino: " + result, EventLogEntryType.Information);

            while (true)
            {
                Thread.Sleep(tempo);
                
                try
                {
                    bool falhou = false;
                    bool construindo = false;
                    string usuFalha = "";
                    string buildFalha = "";
                    string projetoFalha = "";
                    string descCommitFalha = "";

                    foreach (string projeto in projetos)
                    {
                        string url = serverJenkins + "/job/" + projeto + "/api/json";
                        string resp = await RequisitarDaAPI(url);

                        JObject json = JObject.Parse(resp);
                        JArray builds = (JArray)json["builds"];                    

                        foreach (JObject build in builds)
                        {
                            string urlBuild = (string)build["url"] + "/api/json";
                            string respBuild = await RequisitarDaAPI(urlBuild);

                            JObject jsonBuild = JObject.Parse(respBuild);

                            bool _construindo = (bool)jsonBuild["building"];
                            string status = (string)jsonBuild["result"];

                            if (_construindo)
                            {
                                construindo = true;
                                break;
                            } else if (status == SUCESSO)
                            {
                                break;
                            } else if (status == FALHA)
                            {
                                falhou = true;
                                projetoFalha = projeto;
                                buildFalha = (string)build["number"];

                                JArray changes = (JArray)jsonBuild["changeSet"]["items"];

                                if (changes.Count > 0)
                                {
                                    usuFalha = (string)jsonBuild["changeSet"]["items"][0]["author"]["fullName"];                                
                                    descCommitFalha = (string)jsonBuild["changeSet"]["items"][0]["msg"];
                                } else
                                {
                                    usuFalha = "Nao informado";
                                    descCommitFalha = "Nao informado";
                                }                                
                            }
                        }

                        if (falhou) {break;}
                    }

                    result = await EnviarResultadoParaOArduino(hostArduino, falhou, 
                        construindo, usuFalha, buildFalha, projetoFalha, descCommitFalha);

                    EventLog.WriteEntry(result, EventLogEntryType.Information);
                }
                catch (Exception e)
                {
                    //System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)
                    EventLog.WriteEntry("Erro:" + e.StackTrace, EventLogEntryType.Error);
                }

            }
        }

        async static Task<string> EnviarResultadoParaOArduino(string hostArduino, bool falhou, bool construindo, string usuFalha, string buildFalha, string projetoFalha, string descCommitFalha)
        {
            string corpo = "";

            if (falhou) {

                string usuario = usuFalha;
                if (usuario.Length > 11)
                {
                    usuario = usuario.Substring(0, 11);
                }

                string projeto = projetoFalha;
                if (projeto.Length > 17)
                {
                    projeto = projeto.Substring(0, 17);
                }
            
                string descricao = descCommitFalha;
                if (descricao.Length > 50)
                {
                    descricao = descricao.Substring(0, 50);
                }
                //descricao = await RemoverAcentuacao(descricao);


                /*string usuario = usuFalha.Substring(0, 11);
                string projeto = projetoFalha.Substring(0, 17);                          
                string descricao = await RemoverAcentuacao(descCommitFalha.Substring(0, 50));*/

                corpo = "{\"stat\":\"f\",\"usu\":\"" + usuario + "\",\"build\":" + 
                    buildFalha + ",\"proj\":\"" + projeto + "\",\"desc\":\"" + descricao + "\"}";
            }
            else if (construindo) {
                corpo = "{\"stat\":\"c\"}";
            }
            else {
                corpo = "{\"stat\":\"s\"}";
            }
           
            string result = await EnviarParaAPI(hostArduino, corpo);

            return result;
        }

        async static Task<string> EnviarArduinoInicioFim(char stat)
        {
            try
            {
                string result = await EnviarParaAPI(hostArduino, "{\"stat\":\"" + stat + "\"}");
                return result;
            }
            catch (Exception e)
            {
                return e.StackTrace;
            }
        }

        /*async static Task<string> RemoverAcentuacao (string txt)
        {
            string newTxt = txt;
            string strOldChars = "áéíóúàèìòùâêîôûãõçÁÉÍÓÚÀÈÌÒÙÂÊÎÔÛÃÕÇ";
            string strNewChars = "aeiouaeiouaeiouaocAEIOUAEIOUAEIOUAOC";
            char[] oldChars = strOldChars.ToCharArray();
            char[] newChars = strNewChars.ToCharArray();

            for (int i = 0; i < oldChars.Length -1; i++)
            {
                newTxt = newTxt.Replace(oldChars[i], newChars[i]);
            }

            return newTxt;
        }*/

        async static Task<string> RequisitarDaAPI(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                using (HttpResponseMessage response = await client.GetAsync(url))
                {
                    using (HttpContent content = response.Content)
                    {
                        string result = await content.ReadAsStringAsync();
                        return result;
                    }
                }
            }
        }

        async static Task<string> EnviarParaAPI(string url, string corpo)
        {
            using (HttpClient client = new HttpClient())
            {
                using (HttpContent contentPost = new StringContent(corpo))
                {
                    using (HttpResponseMessage response = await client.PostAsync(url, contentPost))
                    {
                        using (HttpContent contentResp = response.Content)
                        {
                            string result = await contentResp.ReadAsStringAsync();
                            return result;
                        }
                    }
                }
            }
        }
    }
}
