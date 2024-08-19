using System.Drawing;
using System.IO.Compression;
using System.Net.NetworkInformation;

namespace BBox_ModBusWorker
{
    /// <summary>
    /// Classe do serviço de envio de dados
    /// </summary>
    public class DataSendRequest : BackgroundService
    {
        private readonly ILogger<DataSendRequest> _logger;
        private readonly IConfiguration _configuration;

        private readonly string? _clientId;
        private readonly string? _shipId;

        public DataSendRequest(ILogger<DataSendRequest> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            _clientId = _configuration.GetValue<string>("ShipSettings:ClientID", defaultValue: "").ToUpperInvariant();
            _shipId = _configuration.GetValue<string>("ShipSettings:ShipID", defaultValue: "").ToUpperInvariant();
        }

        /// <summary>
        /// Main task
        /// </summary>
        /// <param name="stoppingToken">passa flag de cancelamento</param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    //_logger.LogInformation("Initiating CSV agregation...\n");
                    // chama método de agragação de ficheiros CSV
                    await AggregateCSVData(); 
                }
                catch (Exception ex)
                {
                    //_logger.LogError($"Something went wrong when merging the files: {ex.Message}");
                    Console.WriteLine($"Something went wrong when merging the files: {ex.Message}");
                    try
                    {
                        await SendAliveRequest(_configuration, false);
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine($"It was not possible to send alive request: {ex2.Message}");
                    }
                    //await SendAliveRequest(_configuration, false);
                }
                await Task.Delay(_configuration.GetValue<int>("ApiRequestSettings:SendingInterval"), stoppingToken);
            }
        }

        #region Métodos privados

        /// <summary>
        /// Método de agregação de CSVs de variáveis
        /// </summary>
        /// <returns></returns>
        private async Task AggregateCSVData()
        {

            var currDate = DateTime.Now;
            var month = currDate.Month.ToString().PadLeft(2, '0');
            var day = currDate.Day.ToString().PadLeft(3, '0');

            // formação do nome das pastas a consultar
            var mainDirectory = Path.Combine(Environment.CurrentDirectory, $"vars");

            // consulta as pastas de equipamentos na raíz (vars/)
            var equi_directories = Directory.GetDirectories(mainDirectory);


            // Para cada diretoria de equipamentos
            foreach (var dir in equi_directories)
            {
                // consulta as pastas de variáveis na pasta do equipamento atual
                var var_directories = Directory.GetDirectories(Path.GetFullPath(dir));

                // Para cada diretoria de variaveis 
                foreach (var varDir in var_directories)
                {
                    // forma nome da pasta do mês
                    var monthDir = Path.Combine(Path.GetFullPath(varDir), $"{currDate.Year}/{month}");
                    List<string> csvFiles = new List<string>();
                    string dailyData = String.Empty;

                    // Se pasta mensal existir
                    if (Directory.Exists(monthDir))
                    {
                        // Para cada pasta "diária" na pasta mensal
                        foreach (var dayDir in Directory.GetDirectories(monthDir))
                        {
                            // devolve os ficheiros diarios de cada pasta diaria
                            csvFiles.AddRange(Directory.GetFiles(dayDir, "*.csv"));
                        }

                        //_logger.LogInformation($"Processing directory: {varDir}");

                        // forma nome base para ficheiros mensais
                        var monthFile = $"{_clientId}_{_shipId}-{Path.GetFileName(dir)}_{Path.GetFileName(varDir)}_M{currDate.Year}{month}";

                        // Caso existam ficheiros
                        if (csvFiles.Any())
                        {
                            // forma nome e caminho para ficheiro .zip
                            var zipFile = $"{monthFile}.zip";
                            var zipFilePath = Path.Combine(monthDir, zipFile);
                            
                            // para cada ficheiro csv
                            foreach (var csvFile in csvFiles.Order())
                            {
                                //_logger.LogInformation($"Reading CSV file: {csvFile}");

                                try
                                {
                                    // exporta texto
                                    var csvContents = File.ReadAllText(csvFile);
                                    // Agrega conteúdo 
                                    dailyData += csvContents;

                                }
                                catch (Exception ex)
                                {
                                    //_logger.LogError($"Error when reading CSV data: {ex.Message}");
                                    Console.WriteLine($"Error when reading CSV data: {ex.Message}");
                                }
                            }

                            try
                            {
                                // Chama método de validação de origem à alteração do ficheiro mensal
                                Worker.validateKeyFileWrite(Path.Combine(monthDir, $"{monthFile}.csv"), _clientId, _shipId, dailyData, currDate, true);

                            }
                            catch (Exception ex)
                            {
                                //_logger.LogError($"Error when aggregating CSV data: {ex.Message}");
                                Console.WriteLine($"Error when aggregating CSV data: {ex.Message}");
                            }


                            try
                            {
                                // se atualmente existir um ficheiro mensal .zip na pasta, elimina
                                if (File.Exists(zipFilePath)) File.Delete(zipFilePath);

                                // comprime o ficheiro .csv para .zip
                                using (var zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create))
                                {
                                    zipArchive.CreateEntryFromFile(Path.Combine(monthDir, $"{monthFile}.csv"), $"{monthFile}.csv");
                                }

                                // Invoca método de envio do ficheiro .zip
                                await SendZipFile(zipFilePath);
                                // Invoca método de envio de sinal de execução (alive)
                                await SendAliveRequest(_configuration, false);
                                await SendAliveRequest(_configuration,true,"Monthly_Export");

                            }
                            catch (Exception ex)
                            {
                                //_logger.LogError($"It was not possible to process zip file : {ex.Message}");
                                Console.WriteLine($"It was not possible to process zip file : {ex.Message}");
                                try
                                {
                                    await SendAliveRequest(_configuration, false);
                                }
                                catch (Exception ex2)
                                {
                                    Console.WriteLine($"It was not possible to send alive request: {ex2.Message}");
                                }
                            }

                        }

                    }                                 
                }
            }
        }

        /// <summary>
        /// Método de envio de um ficheiro via API 
        /// </summary>
        /// <param name="filePath">Passa caminho do ficheiro</param>
        /// <returns></returns>
        private async Task SendZipFile(string filePath)
        {
            using (var client = new HttpClient())
            using (var content = new MultipartFormDataContent())
            using (var filestream = new FileStream(filePath, FileMode.Open))
            {
                var year = DateTime.Now.Year;
                var month = DateTime.Now.Month.ToString().PadLeft(2, '0');

                content.Add(new StreamContent(filestream), "bfile", Path.GetFileName(filePath));
                content.Add(new StringContent(_clientId), "idclient");
                content.Add(new StringContent(_configuration.GetValue<string>("ApiRequestSettings:APIkey")), "idkey");
                content.Add(new StringContent($"M{year}{month}"), "period");

                var response = await client.PostAsync($"{_configuration.GetValue<string>("ApiRequestSettings:DataUploadApiUrl")}", content);

                if (response.IsSuccessStatusCode)
                {
                    //_logger.LogInformation($"Zip file sucessfully sent.");
                }
                else
                {
                    //_logger.LogError($"It was not possible to send zip file! - {response.StatusCode}");
                    Console.WriteLine($"It was not possible to send zip file! - {response.StatusCode}");
                }
            }
        }

        /// <summary>
        /// Método que devolve listagem de endereços IP associados à máquina
        /// </summary>
        /// <returns>Retorna um array de strings</returns>
        private static string[] GetLocalIPAdress()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            var localIPs = interfaces
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback /*&& i.NetworkInterfaceType != NetworkInterfaceType.Wireless80211*/)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(addr => addr.Address.ToString())
                .ToArray();


            return localIPs;
        }

        #endregion

        #region Métodos Publicos

        /// <summary>
        /// Método para envio de sinal alarmístico, garantindo a prova de execução do serviço
        /// </summary>
        /// <param name="configuration">Passa acesso à configuração</param>
        /// <param name="app">Boleano para sinal alive de aplicação</param>
        /// <param name="idApp">opcional nome da aplicação que irá no envio</param>
        /// <returns></returns>
        public static async Task SendAliveRequest(IConfiguration configuration, bool app,string idApp = "")
        {
            // Nova instânci Http
            using (var client = new HttpClient())
            using (var content = new MultipartFormDataContent())
            {
                // passa parametros da configuração para o envio de sinal
                content.Add(new StringContent(configuration.GetValue<string>("ShipSettings:ClientID").ToUpperInvariant()), "idclient");
                content.Add(new StringContent(configuration.GetValue<string>("ApiRequestSettings:APIkey")),"idkey");
                content.Add(new StringContent(configuration.GetValue<string>("ShipSettings:EquipmentID").ToUpperInvariant()),"idequipment");

                // Caso seja um sinal associado a uma aplicação
                if (app)
                {
                    // Adiciona o nome da aplicação
                    //content.Add(new StringContent($"{configuration.GetValue<string>("ShipSettings:ShipID").ToUpperInvariant()}_{idApp}"),"idapplication");
                    content.Add(new StringContent($"{idApp}"),"idapplication");
                } 
                else
                {
                    // Caso contrário, devolve lista de Ips disponíveis da máquina 
                    var ipList = GetLocalIPAdress();
                    content.Add(new StringContent(string.Join(", ", ipList)), "ip_list");

                }
                
                // Resposta de envio
                // Altera endpoint consoante boleano app
                var response = await client.PostAsync($"{ 
                    (
                        app ? configuration.GetValue<string>("ApiRequestSettings:AliveAppRequestApiUrl") 
                        : configuration.GetValue<string>("ApiRequestSettings:AliveRequestApiUrl")
                    )
                }", content);

                if (response.IsSuccessStatusCode)
                {
                }
                else
                {
                    Console.WriteLine($"Something went wrong while sending request - {response.StatusCode}");
                }
              
            }
        }

        /// <summary>
        /// Método para envio de últimos valores de variáveis 
        /// </summary>
        /// <param name="configuration">passa acesso à configuração</param>
        /// <param name="valuesString">Passa string com variáveis a serem atualizadas</param>
        /// <returns></returns>
        public static async Task SendLastValueRequest(IConfiguration configuration, string valuesString)
        {
            using (var client = new HttpClient())
            using (var content = new MultipartFormDataContent())
            {
                content.Add(new StringContent(configuration.GetValue<string>("ShipSettings:ClientID").ToUpperInvariant()), "idclient");
                content.Add(new StringContent(configuration.GetValue<string>("ApiRequestSettings:APIkey")), "idkey");
                content.Add(new StringContent(valuesString), "vars_last_value");

                var response = await client.PostAsync($"{configuration.GetValue<string>("ApiRequestSettings:LastValueRequestApiUrl")}", content);

                if (response.IsSuccessStatusCode)
                {
                    //_logger.LogInformation($"Zip file sucessfully sent.");
                }
                else
                {
                    //_logger.LogError($"It was not possible to send zip file! - {response.StatusCode}");
                    Console.WriteLine($"It was not possible to send last values - {response.StatusCode}");
                }
            }
        }

        #endregion

    }
}
