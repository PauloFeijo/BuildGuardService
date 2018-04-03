using System;
using System.Diagnostics;
using System.Net.Http;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ini.Net;
using System.Text;

namespace BuildGuardService
{
    public partial class BuildGuardService : ServiceBase
    {
        private string pathExe;
        private string hostArduino;
        private string serverJenkins;
        private int tempoCiclo;
        private string pathAuditoria;
        private bool auditar;
        private string[] projetos;
        private Thread thread;

        public BuildGuardService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            ThreadStart start = new ThreadStart(ConsultarAPIJenkinsEnviarArduino);
            thread = new Thread(start);
            thread.Start();
        }

        protected override void OnStop()
        {
            thread.Suspend();
            string result = EnviarArduinoInicioFim('p');
            Auditoria("Envio de parada ao arduino: " + result);
        }

        private void Inicializar()
        {
            try
            {
                pathExe = System.AppDomain.CurrentDomain.BaseDirectory.ToString();
                pathAuditoria = pathExe + "\\auditoria.txt";

                IniFile ini = new IniFile(pathExe + "\\Config.ini");
                
                serverJenkins = ini.ReadString("CONFIG", "Jenkins");
                tempoCiclo = ini.ReadInteger("CONFIG", "Temposegundos") * 1000;
                hostArduino = ini.ReadString("CONFIG", "Arduino");
                auditar = ini.ReadBoolean("CONFIG", "Auditar");
                
                projetos      = System.IO.File.ReadAllLines(pathExe + "\\Projetos.ini");
            }
            catch (Exception e)
            {
                throw new Exception("Erro ao inicializar parâmetros: "+e.StackTrace);
            }
        }

        private void ConsultarAPIJenkinsEnviarArduino()
        {                       
            const string SUCESSO = "SUCCESS";
            const string FALHA = "FAILURE";

            Inicializar();

            string result = EnviarArduinoInicioFim('i');
            Auditoria("Envio de início ao arduino: " + result);

            while (true)
            {
                Thread.Sleep(tempoCiclo);
                
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
                        string resp = RequisitarDaAPI(url);

                        Auditoria("Resultado api Jenkins:\n" + resp);

                        JObject json = JObject.Parse(resp);
                        JArray builds = (JArray)json["builds"];                    

                        foreach (JObject build in builds)
                        {
                            string urlBuild = (string)build["url"] + "/api/json";
                            string respBuild = RequisitarDaAPI(urlBuild);

                            Auditoria("Resultado api Jenkins build: "+urlBuild+"\n" + respBuild);

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

                    result = EnviarResultadoParaOArduino(falhou, construindo, usuFalha, 
                        buildFalha, projetoFalha, descCommitFalha);

                    Auditoria("Resultado api arduino:\n" + result);
                }
                catch (Exception e)
                {
                    Auditoria("Erro: " + e.StackTrace);                    
                }                
            }
        }

        private string EnviarResultadoParaOArduino(bool falhou, bool construindo, string usuFalha, string buildFalha, string projetoFalha, string descCommitFalha)
        {
            string corpo = "";

            if (falhou) {

                string usuario = usuFalha.Replace("NT\\", "");

                if (usuario.Length > 11)
                {
                    usuario = usuario.Substring(0, 10);
                }
                usuario = RemoverAcentuacao(usuario);

                string projeto = projetoFalha;
                if (projeto.Length > 17)
                {
                    projeto = projeto.Substring(0, 17);
                }
                projeto = RemoverAcentuacao(projeto);

                string descricao = descCommitFalha;
                if (descricao.Length > 50)
                {
                    descricao = descricao.Substring(0, 50);
                }
                descricao = RemoverAcentuacao(descricao);

                string build = buildFalha;
                if (build.Length > 4)
                {
                    build = build.Substring(build.Length - 4, 5);                
                } else
                {
                    while (build.Length < 4)
                    {
                        build = " " + build;
                    }
                }

                corpo = "{\"stat\":\"f\",\"usu\":\"" + usuario + "\",\"build\":\"" +
                    build + "\",\"proj\":\"" + projeto + "\",\"desc\":\"" + descricao + "\"}";               
            }
            else if (construindo) {
                corpo = "{\"stat\":\"c\"}";
            }
            else {
                corpo = "{\"stat\":\"s\"}";
            }

            Auditoria("Corpo arduino: " + corpo);

            string result = EnviarParaAPI(hostArduino, corpo);

            return result;
        }

        private string EnviarArduinoInicioFim(char stat)
        {
            try
            {
                string result = EnviarParaAPI(hostArduino, "{\"stat\":\"" + stat + "\"}");
                return result;
            }
            catch (Exception e)
            {
                return e.StackTrace;
            }
        }

        private string RemoverAcentuacao (string txt)
        {
            string newTxt = txt;
            string strOldChars = "áéíóúàèìòùâêîôûãõçÁÉÍÓÚÀÈÌÒÙÂÊÎÔÛÃÕÇ\\/\"";
            string strNewChars = "aeiouaeiouaeiouaocAEIOUAEIOUAEIOUAOC--'";
            char[] oldChars = strOldChars.ToCharArray();
            char[] newChars = strNewChars.ToCharArray();

            for (int i = 0; i < oldChars.Length -1; i++)
            {
                newTxt = newTxt.Replace(oldChars[i], newChars[i]);
            }

            return newTxt;
        }

        private string RequisitarDaAPI(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                using (Task<HttpResponseMessage> responseTask = client.GetAsync(url))
                {
                    while (!responseTask.IsCompleted) { }
                    using (HttpResponseMessage response = responseTask.Result)
                    {
                        using (HttpContent content = response.Content)
                        {
                            using (Task<string> resultTask = content.ReadAsStringAsync())
                            {
                                while (!resultTask.IsCompleted) { }
                                return resultTask.Result;
                            }                                
                        }
                    }
                }
            }
        }

        private string EnviarParaAPI(string url, string corpo)
        {
            using (HttpClient client = new HttpClient())
            {
                using (HttpContent contentPost = new StringContent(corpo, Encoding.UTF8, "application/json"))
                {
                    using (Task<HttpResponseMessage> responseTask = client.PostAsync(new Uri(url), contentPost))
                    {
                        while (!responseTask.IsCompleted) { }

                        if (responseTask.Status != TaskStatus.RanToCompletion)
                        {
                            return responseTask.Status.ToString();
                        }
                        using (HttpResponseMessage response = responseTask.Result)
                        {
                            using (HttpContent contentResp = response.Content)
                            {
                                using (Task<string> resultTask = contentResp.ReadAsStringAsync())
                                {
                                    while (!responseTask.IsCompleted) { }
                                    string result = resultTask.Result;
                                    return result;
                                }
                            }
                        }
                    }
                }
            }
        }

        private void Auditoria(string txt)
        {
            if (auditar)
            {
                string horario = DateTime.Now.ToShortDateString() + " " + DateTime.Now.ToLongTimeString() + ": ";
                System.IO.File.AppendAllText(pathAuditoria, horario + txt + "\n\n");
            }
        }
    }
}
