using System.Security.Cryptography;
using System.Text;

namespace BBox_ModBusWorker
{
    #region Simular inserção

    /// <summary>
    /// Class para simulação de inserção de dados
    /// </summary>
    public static class EquipmentSim
    {

        /// <summary>
        /// Método de inserção de dados no ficheiro de base de dados
        /// </summary>
        /// <param name="connString">Passa a connection string</param>
        public static void InsertValues(string connString)
        {
            // DatabaseHelper.InitializeDatabase(connString);

            // Insert DataSource
            DatabaseHelper.InsertDataSource(new DataSource
            {
                Name = "SCR_Automation",
                Protocol = "modbus",
                IPAddress = "127.0.0.1",
                Port = "502",
                BaudRate = 9600

            }, connString);

            // Insert Equipamento
            DatabaseHelper.InsertEquipment(new Equipment
            {
                Name = "UREA",
                BoemName = "UREA",
                ReadRate = 10000,
                DataSourceId = 1

            }, connString);

            // Insert Variável
            DatabaseHelper.InsertVariable(new Variable
            {
                Name = "Temp",
                BoemName = "temp",
                StartAddress = 10,
                NumRegisters = 2,
                EquipmentId = 1

            }, connString);

        }


    }

    #endregion

    /// <summary>
    /// Classe para a execução do serviço de leitura
    /// </summary>
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;
        private readonly string? _connString;
        private readonly string? _clientId;
        private readonly string? _shipId;
        private readonly bool _dbSource;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            
            _connString = _configuration.GetConnectionString("SQLite");
            _clientId = _configuration.GetValue<string>("ShipSettings:ClientID", defaultValue: "").ToUpperInvariant();
            _shipId = _configuration.GetValue<string>("ShipSettings:ShipID", defaultValue: "").ToUpperInvariant();
            _dbSource = _configuration.GetValue<bool>("ShipSettings:DBsource");

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
                // inicia BD
                if (_dbSource) await DatabaseHelper.InitializeDatabase(_connString);
                //EquipmentSim.InsertValues(connString);


                _logger.LogInformation("\n*********  Worker running at: {time} ***********", DateTime.Now);

                // lista de DataSources
                List<DataSource> data_list;
                // leitura por base de dados ou config
                if (!_dbSource) data_list = GetDataSourcesConfig();
                else data_list = DatabaseHelper.GetDataSources(_connString);

                var protocolTasks = new List<Task>();

                // Para cada fonte de dados
                foreach (var source in data_list)
                {
                    
                    // Gera tarefa para cada protocolo
                    Task protocolTask = HandleProtocol(source);
                    protocolTasks.Add(protocolTask);
                   
                }

                await Task.WhenAll(protocolTasks);

                await Task.Delay(1000, stoppingToken);
            }                             
        }

        #region Métodos públicos

        /// <summary>
        /// Método de validação de origem à alteração dos ficheiros
        /// Escrita e cálculo de nova chave 
        /// </summary>
        /// <param name="filePath">Caminho do ficheiro a ser alterado</param>
        /// <param name="clientId">Id configurado do cliente</param>
        /// <param name="shipId">Id configurado do navio</param>
        /// <param name="result">Valor a escrever no ficheiro</param>
        /// <param name="monthlyFile">booleano caso seja ficheiro mensal</param>
        public static void validateKeyFileWrite(string filePath, string clientId, string shipId, string result, DateTime timeStamp, bool monthlyFile = false)
        {
            var currentDate = timeStamp;

            // Caso não seja ficheiro mensal
            if (!monthlyFile)
            {
                // Se já existe o ficheiro a alterar e o ficheiro de chave associado
                if (File.Exists(filePath) && File.Exists($"{filePath.Split(".")[0]}.txt"))
                {
                    // lê o conteúdo do .csv 
                    var csvContents = File.ReadAllText($"{filePath.Split(".")[0]}.csv");
                    // Cálculo da chave
                    var calcKey = calculateSHA1(clientId + currentDate.Year + calculateSHA1(csvContents + shipId));
                    // lê conteúdo do ficheiro chave
                    var keyText = File.ReadAllText($"{filePath.Split(".")[0]}.txt").Trim();

                    // Se cálculo coincidir com a chave escrita
                    if (String.Equals(calcKey, keyText))
                    {

                        //abre o ficheiro e escreve o novo valor (incremental)
                        using (StreamWriter sw = new StreamWriter(filePath, true))
                        {
                            sw.WriteLine($"{currentDate:yyyy-MM-dd HH:mm:ss},,{result}");
                        }

                        // Lê o novo conteúdo .csv
                        csvContents = File.ReadAllText(filePath);
                        // Calcula nova chave
                        calcKey = calculateSHA1(clientId + currentDate.Year + calculateSHA1(csvContents + shipId));
                        // Escreve a nova chave calculada no ficheiro
                        using (var sw = new StreamWriter($"{filePath.Split(".")[0]}.txt", false))
                        {
                            sw.WriteLine(calcKey);
                        }
                    }
                    // Se o cálculo não coincidir com a chave escrita
                    else
                    {
                        // Escreve novamente a mesma chave para que seja disparado o evento
                        using (var sw = new StreamWriter($"{filePath.Split(".")[0]}.txt", false))
                        {
                            sw.WriteLine(keyText);
                        }
                    }

                }
                // Se o ficheiro a alterar ou o ficheiro chave não existirem
                else
                {
                    // Se o ficheiro a alterar existir é eliminado
                    if (File.Exists(filePath)) File.Delete(filePath);

                    // Abre o ficheiro e escreve o novo valor (incremental)
                    using (StreamWriter sw = new StreamWriter(filePath, true))
                    {
                        sw.WriteLine($"{currentDate:yyyy-MM-dd HH:mm:ss},,{result}");
                    }
                    // Lê o conteúdo do ficheiro .csv
                    var csvContents = File.ReadAllText(filePath);
                    // Calcula a chave 
                    var calcKey = calculateSHA1(clientId + currentDate.Year + calculateSHA1(csvContents + shipId));
                    // escreve a chave calculada no ficheiro
                    using (var sw = new StreamWriter($"{filePath.Split(".")[0]}.txt", false))
                    {
                        sw.WriteLine(calcKey);
                    }

                }
            }
            // Caso seja o ficheiro mensal a alterar
            else
            {
                // Se já existe o ficheiro a alterar e o ficheiro de chave associado 
                if (File.Exists(filePath) && File.Exists($"{filePath.Split(".")[0]}.txt"))
                {
                    // Lê o conteúdo do ficheiro .csv
                    var csvContents = File.ReadAllText($"{filePath.Split(".")[0]}.csv");

                    // Calcula a chave 
                    var calcKey = calculateSHA1(clientId + currentDate.Year + calculateSHA1(csvContents + shipId));

                    // lê conteúdo do ficheiro chave
                    var keyText = File.ReadAllText($"{filePath.Split(".")[0]}.txt").Trim();

                    // Se cálculo coincidir com a chave escrita
                    if (String.Equals(calcKey, keyText))
                    {
                        // Elimina o ficheiro existente
                        File.Delete(filePath);

                        //abre o ficheiro e escreve novo valor (incremental)
                        using (StreamWriter sw = new StreamWriter(filePath, true))
                        {
                            sw.Write(result);
                        }
                        // Lê o novo conteúdo .csv
                        csvContents = File.ReadAllText(filePath);

                        // Calcula nova chave
                        calcKey = calculateSHA1(clientId + currentDate.Year + calculateSHA1(csvContents + shipId));

                        // Escreve a nova chave calculada no ficheiro
                        using (var sw = new StreamWriter($"{filePath.Split(".")[0]}.txt", false))
                        {
                            sw.WriteLine(calcKey);
                        }
                    }
                    // Se o cálculo não coincidir com a chave escrita 
                    else
                    {
                        // Escreve novamente a mesma chave para que seja disparado o evento
                        using (var sw = new StreamWriter($"{filePath.Split(".")[0]}.txt", false))
                        {
                            sw.WriteLine(keyText);
                        }
                    }

                }
                // Se o ficheiro a alterar ou o ficheiro chave não existirem
                else
                {
                    // Se o ficheiro a alterar existir é eliminado
                    if (File.Exists(filePath)) File.Delete(filePath);

                    //abre o ficheiro e escreve o novo valor (incremental)
                    using (StreamWriter sw = new StreamWriter(filePath, true))
                    {
                        sw.Write(result);
                    }

                    // Lê o conteúdo do ficheiro .csv
                    var csvContents = File.ReadAllText(filePath);

                    // Calcula a chave 
                    var calcKey = calculateSHA1(clientId + currentDate.Year + calculateSHA1(csvContents + shipId));

                    // Escreve a chave calculada no ficheiro
                    using (var sw = new StreamWriter($"{filePath.Split(".")[0]}.txt", false))
                    {
                        sw.WriteLine(calcKey);
                    }

                }
            }
        }

        #endregion

        #region Métodos privados

        /// <summary>
        /// Método para chamada de instancia do protocolo
        /// </summary>
        /// <param name="source">Passa a fonte de dados</param>
        /// <returns></returns>
        private async Task HandleProtocol(DataSource source)
        {

            // Consoante o protocolo, é escolhida a forma de leitura por classe              
            ProtocolHandler handler = Enum.Parse<ProtocolType>(source.Protocol.ToLowerInvariant()) switch
            {
                ProtocolType.modbus => new ModBusHandler(_configuration, _logger),
                ProtocolType.nmea => new NmeaHandler(_configuration, _logger),
                _ => new ModBusHandler(_configuration, _logger)

            };

            
            // Inicia gestor de protocolo
            await handler.Handle(source);

        }


        /// <summary>
        /// Faz a leitura de Datasources registadas no ficheiro de configuração 
        /// </summary>
        /// <returns>Devolve uma lista de Datasources <see cref="DataSource"/></returns>
        private List<DataSource> GetDataSourcesConfig()
        {
            // A cada datasource associa à classe
            return _configuration.GetSection("EquipmentSettings:DataSources")
                .GetChildren()
                .Select(ds => new DataSource
                {
                    Id = int.Parse(ds["Id"]),
                    Name = ds["Name"],
                    Protocol = ds["Protocol"],
                    IPAddress = ds["IPAddress"],
                    Port = ds["Port"].ToUpperInvariant(),
                    BaudRate = int.Parse(ds["BaudRate"])
                })
                .ToList();
        }

        /// <summary>
        /// Faz a leitura de Equipamentos registados no ficheiro de configuração
        /// </summary>
        /// <param name="datasourceId">passa o id da datasource</param>
        /// <returns>Devolve lista de Equipamentos <see cref="Equipment"/></returns>
        public static List<Equipment> GetEquipmentsConfig(int datasourceId, IConfiguration _configuration)
        {
            // a cada equipamento ligado à datasource, associa à classe
            return _configuration.GetSection("EquipmentSettings:Equipamentos")
            .GetChildren()
            .Where(eq => int.Parse(eq["DataSourceId"]) == datasourceId)
            .Select(eq => new Equipment
            {
                Id = int.Parse(eq["Id"]),
                Name = eq["Name"],
                BoemName = eq["BoemName"],
                ReadRate = int.Parse(eq["ReadRate"]),
                DataSourceId = datasourceId,
            })
            .ToList();
        }

        /// <summary>
        /// Faz a leitura de variáveis registadas no ficheiro de configuração
        /// </summary>
        /// <param name="equipmentId">passa o id do equipamento</param>
        /// <returns>Devolve lista de variáveis <see cref="Variable"/></returns>
        public static List<Variable> GetVariablesConfig(int equipmentId, IConfiguration _configuration)
        {
            // a cada variável ligado ao equipamento, associa à classe
            return _configuration.GetSection("EquipmentSettings:Variaveis")
                .GetChildren()
                .Where(varE => int.Parse(varE[$"EquipmentId"]) == equipmentId)
                .Select(varE => new Variable
                {
                    Id = int.Parse(varE["Id"]),
                    Name = varE["Name"],
                    BoemName = varE["BoemName"],
                    StartAddress = ushort.Parse(varE["StartAddress"]),
                    NumRegisters = ushort.Parse(varE["NumRegisters"]),
                    DataType = varE["DataType"],
                    EquipmentId = equipmentId,
                })
                .ToList();
        }


        /// <summary>
        /// Método para calcular uma chave SHA1
        /// </summary>
        /// <param name="input">Passa uma string como argumento</param>
        /// <returns>Devolve string da chave calculada</returns>
        private static string calculateSHA1(string input)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input); // Conversão para bytes 
                byte[] bytes= sha1.ComputeHash(inputBytes); // cálculo da chave
                return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant(); // converte para string e devolve
            }
        }


        #endregion

    }
}