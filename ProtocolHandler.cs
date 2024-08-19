using Modbus.Device;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;

namespace BBox_ModBusWorker
{
    /// <summary>
    /// Listagem dos tipos de protocolos
    /// </summary>
    public enum ProtocolType
    {
        modbus,
        nmea
    }

    #region Classes auxiliares

    public class StringValuesWrapper
    {
        public string Value { get; set; }

        public StringValuesWrapper(string initialValue)
        {
            Value = initialValue;
        }
    }

    #endregion

    #region Classe Base
    /// <summary>
    /// Classe base para as classes de protocolos
    /// </summary>
    public abstract class ProtocolHandler
    {
        protected string? _clientId => _configuration.GetValue<string>("ShipSettings:ClientId").ToUpperInvariant();
        protected string? _shipId => _configuration.GetValue<string>("ShipSettings:ShipId").ToUpperInvariant();

        protected bool _dbSource => _configuration.GetValue<bool>("ShipSettings:DBsource");

        protected string _connString => _configuration.GetConnectionString("SQLite");

        protected readonly IConfiguration? _configuration;
        protected readonly ILogger _logger;

        /// <summary>
        /// Constructor para a passagem da configuração e logger do serviço 
        /// </summary>
        /// <param name="configuration">Configurações definidas no ficheiro</param>
        /// <param name="logger">Logger para escrita em consola</param>
        public ProtocolHandler(IConfiguration? configuration, ILogger logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Método base para a leitura de dados
        /// </summary>
        /// <typeparam name="T">Tipo generico</typeparam>
        /// <param name="dataSource">Passa Datasource <see cref="DataSource"/></param>
        /// <param name="equiName">Passa o nome do equipamento</param>
        /// <param name="var">Passa a variável <see cref="Variable"/></param>
        /// <returns>Devolve um array do tipo T[] (objeto)</returns>
        public virtual async Task<T[]> ReadVariablesAsync<T>(string equiName, Variable var, StringValuesWrapper valuesString, string sentence = "")
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Método de registo de valores em consola e em ficheiros
        /// </summary>
        /// <param name="setName">Nome do equipamento</param>
        /// <param name="var">Variável <see cref="Variable"/></param>
        /// <param name="registers">Lista registos a ler</param>
        public abstract string LogValues<T>(string setName, Variable var, T[] registers);

        /// <summary>
        /// Método para gestão de protocolo como tarefa
        /// </summary>
        /// <param name="dataSource">Passa a fonte de dados</param>
        /// <returns></returns>
        public abstract Task Handle(DataSource dataSource);


    }
    #endregion

    #region Classes Derivadas

    #region ModBus
    /// <summary>
    /// Classe para gerir o protocolo de ModBus
    /// </summary>
    public class ModBusHandler : ProtocolHandler
    {
        private ModbusIpMaster? _master;
        private bool isConnected = false;

        public DateTime logDateTime { get; set; }

        /// <summary>
        /// Constructor da classe base
        /// </summary>
        /// <param name="configuration">Parametros de configuração</param>
        /// <param name="logger">Logger para registo em consola</param>
        public ModBusHandler(IConfiguration? configuration, ILogger logger) : base(configuration, logger)
        {
        }

        /// <summary>
        /// Método para registo de dados em .csv
        /// </summary>
        /// <typeparam name="T">Tipo genérico</typeparam>
        /// <param name="setName">Nome do equipamento a referenciar</param>
        /// <param name="var">Passa a variável <see cref="Variable"/></param>
        /// <param name="registers">Passa array do tipo genérico</param>
        /// <exception cref="InvalidOperationException"></exception>
        /// <returns>Retorna string com o valor lido da variável</returns>
        public override string LogValues<T>(string setName, Variable var, T[] registers)
        {
            // variáveis para a formatação da data nos ficheiros
            var currentDate = DateTime.Now; // data atual
            var month = currentDate.Month.ToString().PadLeft(2, '0'); // número mês ex.: 02 (fevereiro)
            var day = currentDate.Day.ToString().PadLeft(3, '0'); // número dia ex.: 024 

            // formatação do nome e path do ficheiro a criar
            var directoryPath = Path.Combine(Environment.CurrentDirectory, $"vars/{setName}/{var.BoemName.ToUpperInvariant()}/{currentDate.Year}/{month}/{day}");
            var fileName = $"{_clientId}_{_shipId}-{setName}_{var.BoemName.ToUpperInvariant()}_{day}.csv";
            var filePath = Path.Combine(directoryPath, fileName);

            // tradução do valor lido da variável
            var output = String.Empty;

            // cria a pasta se não existir
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            // Caso não seja float 
            if (!String.Equals(var.DataType.ToLowerInvariant(), "float"))
            {
                // Para cada casa de variável lida até ao numero de registos configurado
                // Será equiparado a um endereço
                for (int i = 0; i < registers.Length - (var.NumRegisters - 1); i++)
                {
                    // Caso seja booleano
                    if (String.Equals(var.DataType.ToLowerInvariant(), "bool"))
                    {
                        // Divide o nome para varias variáveis no mesmo endereço flutuante.
                        var splitName = var.BoemName.Split(';');

                        // Pela quantidade de registos configurada, regista o número de variáveis no mesmo endereço
                        for (int x = 0; x < var.NumRegisters; x++)
                        {
                            // Constrói o caminho para o ficheiro diário .csv
                            directoryPath = Path.Combine(Environment.CurrentDirectory, $"vars/{setName}/{splitName[x]}/{currentDate.Year}/{month}/{day}");
                            fileName = $"{_clientId}_{_shipId}-{setName}_{splitName[x].ToUpperInvariant()}_{day}.csv";
                            filePath = Path.Combine(directoryPath, fileName);

                            // Cria a pasta se não existir
                            if (!Directory.Exists(directoryPath))
                            {
                                Directory.CreateDirectory(directoryPath);
                            }

                            // Divide o resultado coincidente com o endereço flutuante ( ex.: 50 -> 50.x )
                            int result = (int.Parse(registers[i].ToString()) >> x) & 0x01;
                            //_logger.LogInformation($"\nReceived at {logDateTime} {setName.ToUpperInvariant()} - {splitName[x].ToUpperInvariant()} as bool .{x}: {result != 0}");

                            output = result.ToString();

                            // Chama método para a validação da origem da alteração dos ficheiros
                            Worker.validateKeyFileWrite(filePath, _clientId, _shipId, output, logDateTime);
                        }
                    }
                    // Caso não seja booleano (Integer)
                    else if (String.Equals(var.DataType.ToLowerInvariant(), "int"))
                    {
                        //_logger.LogInformation($"\nReceived at {logDateTime} {setName.ToUpperInvariant()} - {var.Name.ToUpperInvariant()} as int : {registers[i]}");

                        // Irá bastar o número de endereços definido com o número de registos
                        
                        output = registers[i].ToString();

                        // Chama método para a validação da origem da alteração dos ficheiros
                        Worker.validateKeyFileWrite(filePath, _clientId, _shipId, output, logDateTime);
                    }

                    else throw new InvalidOperationException("Invalid type of registers.");
                }
            }
            // Caso seja float
            else
            {
                // Lê cada registo ushort (16 bits), agrupando de 2 em 2
                for (int i = 0; i < registers.Length; i += 2)
                {

                    // se não passa o limite
                    if (i + 1 < registers.Length)
                    {

                        // conversão para bytes com a concatenação do registo atual com o próximo (16 bits + 16 bits = 4 bytes)
                        byte[] bytes = BitConverter.GetBytes((ushort)Convert.ChangeType(registers[i], typeof(ushort)))
                            .Concat(BitConverter.GetBytes((ushort)Convert.ChangeType(registers[i + 1], typeof(ushort)))).ToArray();

                        //if (BitConverter.IsLittleEndian) Array.Reverse(bytes);

                        // Se existerem 4 bytes / 32 bits
                        if (bytes.Length == 4)
                        {
                            float result = BitConverter.ToSingle(bytes, 0); // converte para float
                            //_logger.LogInformation($"Received at {logDateTime} {setName.ToUpperInvariant()} - {var.Name.ToUpperInvariant()} as float: {result.ToString("F3")}");

            
                            output = result.ToString("F3");

                            // Chama método para a validação da origem da alteração dos ficheiros
                            Worker.validateKeyFileWrite(filePath, _clientId, _shipId, output, logDateTime);
                        }
                        else
                        {
                            //_logger.LogError($"Invalid byte array length for float conversion.");
                            Console.WriteLine($"Invalid byte array length for float conversion.");
                        }
                    }
                    else
                    {
                        //_logger.LogError($"Insufficient data to form a pair of ushort values.");
                        Console.WriteLine($"Insufficient data to form a pair of ushort values.");
                    }
                }
            }

            ///// -- Alterado para dentro de cada condição para um tempo de escrita mais preciso -- //////
            // Chama método para a validação da origem da alteração dos ficheiros
            // Worker.validateKeyFileWrite(filePath, _clientId, _shipId, output, logDateTime);

            // Devolve valor
            return output;
        }

        /// <summary>
        /// Método para gestão de protocolo ModBus como tarefa
        /// </summary>
        /// <param name="dataSource">passa fonte de dados <see cref="DataSource"/></param>
        /// <returns></returns>
        public override async Task Handle(DataSource dataSource)
        {
            
            // Enquanto não conseguir estabelecer ligação
            while (!isConnected)
            {
                
                //_logger.LogInformation($"\nConnecting to {dataSource.Name} through {dataSource.IPAddress}:{dataSource.Port} on {DateTime.Now}\n");
                try
                {

                    // É criada a ligação TCP IP com DataSource
                    using (TcpClient client = new TcpClient(dataSource.IPAddress, Convert.ToInt32(dataSource.Port)))
                    using (_master = ModbusIpMaster.CreateIp(client))
                    {

                        /// para cada Datasource, lista equipamentos
                        /// passa a connection string e id da atual DataSource
                        // leitura por base de dados ou config
                        List<Equipment> equi_list = _dbSource ? DatabaseHelper.GetEquipments(_connString, dataSource.Id) : Worker.GetEquipmentsConfig(dataSource.Id, _configuration);

                        // Dicionário para registo da última leitura por equipamento
                        Dictionary<Equipment, DateTime> lastExecTimes = new Dictionary<Equipment, DateTime>();

                        // Timestamp para registo de último envio 
                        DateTime lastSendReqTime = DateTime.Now;

                        // para registo de valores a enviar
                        var valuesString = new StringValuesWrapper(String.Empty);

                        while (true)
                        {
                            logDateTime = DateTime.Now;
                            
                           // Para cada equipamento
                            foreach (var equi in equi_list)
                            {
                                /// para cada equipamento, lista variáveis
                                /// passa a connection string e id do equipamento atual
                                // leitura por base de dados ou config
                                List<Variable> vars_list = _dbSource ? DatabaseHelper.GetVariables(_connString, equi.Id) : Worker.GetVariablesConfig(equi.Id, _configuration);

                                DateTime lastExecTime;
                                // Se existe já um registo da última execução
                                if (lastExecTimes.TryGetValue(equi, out lastExecTime))
                                {
                                    // Se a diferença entre o tempo atual e a última execução for maior
                                    // que o readrate do equipamento
                                    if ((logDateTime - lastExecTime).TotalMilliseconds >= equi.ReadRate)
                                    {
                                        // Adiciona o número de millisegundos definido na configuração
                                        logDateTime = lastExecTime.AddMilliseconds(equi.ReadRate);
                                        
                                        /// lista tasks para cada variável
                                        /// chama método de leitura para cada uma
                                        var readingTasks = vars_list.Select(varE => ReadVariablesAsync<object>(equi.BoemName.ToUpperInvariant(), varE, valuesString)).ToList();
                                        await Task.WhenAll(readingTasks);

                                        // Regista novo tempo da ultima execução por equipamento
                                        lastExecTimes[equi] = logDateTime;
                                        
                                    }

                                }
                                else
                                {
                                    // Se não existir registo da última execução

                                    //logDateTime = lastExecTime.AddMilliseconds(equi.ReadRate);
                                    /// lista tasks para cada variável
                                    /// chama método de leitura para cada uma
                                    var readingTasks = vars_list.Select(varE => ReadVariablesAsync<object>(equi.BoemName.ToUpperInvariant(), varE, valuesString)).ToList();
                                    await Task.WhenAll(readingTasks);

                                    // Regista novo tempo da ultima execução por equipamento
                                    lastExecTimes[equi] = logDateTime;

                                }
                            }


                            // Se a diferença entre o tempo atual e o tempo do último envio for maior que o intervalo definido 
                            if ((logDateTime - lastSendReqTime).TotalMilliseconds > _configuration.GetValue<int>("ApiRequestSettings:DefaultSignalInterval"))
                            {
                                // envia sinal de execução e ultimos valores
                                await DataSendRequest.SendAliveRequest(_configuration, false);
                                await DataSendRequest.SendAliveRequest(_configuration,true,$"Data_Recording_{dataSource.Protocol.ToUpper()}");
                                await DataSendRequest.SendLastValueRequest(_configuration, valuesString.Value);

                                // regista novo tempo do último envio
                                lastSendReqTime = logDateTime;
                                valuesString = new StringValuesWrapper(String.Empty);
                            }

                            await Task.Delay(10);

                            // Existe ligação
                            isConnected = true;
                            
                        }

                        //_logger.LogInformation("Data reading completed successfully.");
                    }

                }

                catch (Exception plcErr)
                {
                    //_logger.LogError($"Error communicating at {DateTime.Now} with {dataSource.Name}: {plcErr.Message}");
                    Console.WriteLine($"Error communicating at {DateTime.Now} with {dataSource.Name}: {plcErr.Message}");
                    try
                    {
                        await DataSendRequest.SendAliveRequest(_configuration, false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"It was not possible to send alive request: {ex.Message}");
                    }
                    
                    isConnected = false;
                    

                }
          
            }
            await Task.Delay(_configuration.GetValue<int>("ShipSettings:ReadingInterval"));
        }

        /// <summary>
        /// Método para leitura de dados ModBus
        /// </summary>
        /// <typeparam name="T">Tipo genérico</typeparam>
        /// <param name="dataSource">Passa datasource <see cref="DataSource"/></param>
        /// <param name="equiName">Passa nome do equipamento</param>
        /// <param name="var">Passa variável <see cref="Variable"/></param>
        /// <param name="valuesString">Passa string para registo de valores</param>
        /// <param name="sentence">passa string opcional para divisão de variáveis</param>
        /// <returns>Devolve array do tipo T[] (objeto)</returns>
        public override async Task<T[]> ReadVariablesAsync<T>(string equiName, Variable var, StringValuesWrapper valuesString,string sentence = "")
        {           
            try
            {
                    
                // Faz a leitura Modbus consoante endereço da variável e número de registos 
                ushort[] uRegisters = await _master.ReadHoldingRegistersAsync(1, var.StartAddress, var.NumRegisters);
                // Convert each ushort value to Task<T>
                var tasks = uRegisters.Select(u => Task.FromResult((T)(object)u)).ToArray();
                // Await all tasks and get the results
                T[] results = await Task.WhenAll(tasks);
                
                // Incrementa string com nome e valor da variável
                valuesString.Value += $"{_clientId}_{_shipId}-{equiName}_{var.BoemName.ToUpperInvariant()}" +
                    $"={LogValues(equiName.ToUpperInvariant(), var, results)};";

                return results;
                          
            }

            catch (Exception ex)
            {
                Console.WriteLine($"\nError reading variable at {logDateTime} {equiName} - {var.Name} : {ex}");
                
                // Tornar falso para repetir ciclo de ligação
                isConnected = false;
                throw;
            }                                   
        }
    }

    #endregion

    #region NMEA
    /// <summary>
    /// Classe para gerir o protocolo NMEA0183
    /// </summary>
    public class NmeaHandler : ProtocolHandler
    {
        public DateTime logDateTime { get; set; }
        private SerialPort? _serialPort;

        /// <summary>
        /// Constructor da classe base
        /// </summary>
        /// <param name="configuration">Parametros de configuração</param>
        /// <param name="logger">Logger para registo em consola</param>
        public NmeaHandler(IConfiguration? configuration, ILogger logger) : base(configuration, logger)
        {
        }

        /// <summary>
        /// Método para fechar o/a porto/a
        /// </summary>
        public void Close()
        {
            // Caso esteja a nulo ou estado aberto, fecha
            if (_serialPort != null && _serialPort.IsOpen) _serialPort.Close();
        }

        /// <summary>
        /// Método para registo de dados em .csv
        /// </summary>
        /// <typeparam name="T">Tipo genérico</typeparam>
        /// <param name="setName">Nome do equipamento a referenciar</param>
        /// <param name="var">Passa a variável <see cref="Variable"/></param>
        /// <param name="registers">Passa array do tipo genérico</param>
        /// <returns>Retorna string com valores das variáveis</returns>
        public override string LogValues<T>(string setName, Variable var, T[] registers)
        {
            // variáveis para a formatação da data nos ficheiros
            var currentDate = DateTime.Now; // data atual
            var month = currentDate.Month.ToString().PadLeft(2, '0'); // número mês ex.: 02 (fevereiro)
            var day = currentDate.Day.ToString().PadLeft(3, '0'); // número dia ex.: 024 

            // formatação do nome e path do ficheiro a criar
            var directoryPath = Path.Combine(Environment.CurrentDirectory, $"vars/{setName}/{var.BoemName.ToUpperInvariant()}/{currentDate.Year}/{month}/{day}");
            var fileName = $"{_clientId}_{_shipId}-{setName}_{var.BoemName}_{day}.csv";
            var filePath = Path.Combine(directoryPath, fileName);

            // cria a pasta se não existir
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var stringVar = String.Empty;

            // Agrega cada valor numa string
            for (int i = 0; i < registers.Length; i++)
            {
                stringVar += registers[i];
            }

            //_logger.LogInformation($"\nReceived at {logDateTime} {setName} - {var.Name.ToUpperInvariant()} as string: {stringVar}");

            // Chama o método de validação da origem de alteração de ficheiros 
            Worker.validateKeyFileWrite(filePath, _clientId, _shipId, stringVar, logDateTime);

            // Devolve valores
            return stringVar;
        }

        /// <summary>
        /// Método para gestão de protocolo NMEA como tarefa
        /// </summary>
        /// <param name="dataSource">passa fonte de dados</param>
        /// <returns></returns>
        public override async Task Handle(DataSource dataSource)
        {
            
            //_logger.LogInformation($"\nConnecting to {dataSource.Name} through :{dataSource.Port} on {DateTime.Now}\n");

            try
            {
                
                // Abre ligação SerialPort COM
                using (SerialPort serialPort = new SerialPort($"/dev/tty{dataSource.Port}"))
                {
                    _serialPort = serialPort;
                    _serialPort.BaudRate = dataSource.BaudRate;
                    
                    _serialPort.Open();
                    _serialPort.DiscardInBuffer();

                    /// para cada Datasource, lista equipamentos
                    /// passa a connection string e id da atual DataSource
                    /// leitura por base de dados ou config
                    List<Equipment> equi_list = _dbSource ? DatabaseHelper.GetEquipments(_connString, dataSource.Id) : Worker.GetEquipmentsConfig(dataSource.Id, _configuration);

                    // Dicionário para registo da última leitura por equipamento
                    Dictionary<Equipment, DateTime> lastExecTimes = new Dictionary<Equipment, DateTime>();

                    // Timestamp para registo de último envio 
                    DateTime lastSendReqTime = DateTime.Now;

                    while (true)
                    {
                        if (!_serialPort.IsOpen)
                        {
               
                            lastExecTimes.Clear();
                            _serialPort.Open();

                        }

                        // Para registo de valores
                        var valuesString = new StringValuesWrapper(String.Empty);

                        logDateTime = DateTime.Now;
                        
                        // Para cada equipamento chama método de gestão
                        foreach (Equipment equi in equi_list)
                        {
                            
                            DateTime lastExecTime;

                            // Se existe já um registo da última execução
                            if (lastExecTimes.TryGetValue(equi, out lastExecTime))
                            {
                                
                                // Se a diferença entre o tempo atual e a última execução for maior
                                // que o readrate do equipamento
                                
                                if ((logDateTime - lastExecTime).TotalMilliseconds >= equi.ReadRate )
                                {
                                    
                                    // Adiciona o número de millisegundos definido na configuração
                                    logDateTime = lastExecTime.AddMilliseconds(equi.ReadRate); ;
                                    await HandleEquipment(dataSource, equi, valuesString);

                                    // Regista novo tempo da ultima execução por equipamento
                                    lastExecTimes[equi] = logDateTime;

                                }
                                
                            }
                            else
                            {
                                
                                //logDateTime = lastExecTime.AddMilliseconds(equi.ReadRate);
                                await HandleEquipment(dataSource, equi, valuesString);
                                lastExecTimes[equi] = logDateTime;
                              
                            }
                            
                        }

                        // Se a diferença entre o tempo atual e o tempo do último envio for maior que o intervalo definido
                        if ((logDateTime - lastSendReqTime).TotalMilliseconds > _configuration.GetValue<int>("ApiRequestSettings:DefaultSignalInterval"))
                        {
                            // envia sinal de execução e ultimos valores
                            await DataSendRequest.SendAliveRequest(_configuration, true, $"Data_Recording_{dataSource.Protocol.ToUpper()}");
                            await DataSendRequest.SendLastValueRequest(_configuration, valuesString.Value);

                            // regista novo tempo do último envio
                            lastSendReqTime = logDateTime;
                        }

                        _serialPort.DiscardInBuffer();
                        
                        await Task.Delay(500);
                    }
                }
                //_logger.LogInformation("Data reading completed successfully.");
            }
            catch (Exception plcErr)
            {
                //_logger.LogError($"Error communicating at {DateTime.Now} with {dataSource.Name}: {plcErr.Message}");
                Console.WriteLine($"Error communicating at {DateTime.Now} with {dataSource.Name}: {plcErr.Message}");
                try
                {
                    await DataSendRequest.SendAliveRequest(_configuration, false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"It was not possible to send alive request: {ex.Message}");
                }
                throw;
                
            }
           
        }

        /// <summary>
        /// Método para leitura de dados
        /// </summary>
        /// <typeparam name="T">Tipo genérico</typeparam>
        /// <param name="dataSource">Passa datasource <see cref="DataSource"/></param>
        /// <param name="equiName">Passa nome do equipamento</param>
        /// <param name="var">Passa variável <see cref="Variable"/></param>
        /// <returns>Devolve array do tipo T[] (objeto)</returns>
        public override async Task<T[]> ReadVariablesAsync<T>(string equiName, Variable var, StringValuesWrapper valuesString, string sentence = "")
        {

            // Divide a trama e passa para um array do tipo object (T[]) 
            var fields = (T[])(object)NmeaParser(sentence, var.StartAddress, var.NumRegisters);

            // Incrementa string com nome e valor da variável
            valuesString.Value += $"{_clientId}_{_shipId}-{equiName}_{var.BoemName.ToUpperInvariant()}" +
                    $"={LogValues(equiName.ToUpperInvariant(), var, fields)};";

            // Devolve o array
            return fields;

        }


        #region Private Methods

        /// <summary>
        /// Método para leitura da trama através do/a porto/a
        /// </summary>
        /// <returns>Devolve uma string</returns>
        private async Task<string> ReadLineAsync()
        {
            //return await Task.Run(() => _serialPort.ReadLine());
            StringBuilder sb = new StringBuilder();

            while (true)
            {
                char c = await Task.Run(() => (char)_serialPort.ReadChar());
                sb.Append(c);

                // Check if the current character is a newline or carriage return
                if (c == '\n' || c == '\r')
                {
                    // If it is, return the accumulated string
                    return sb.ToString();
                }
            }
        }

        /// <summary>
        /// Método para a divisão da trama para campos
        /// </summary>
        /// <param name="sentence">Passa a trama</param>
        /// <param name="address">Passa o endereço da variável</param>
        /// <param name="fieldNumber">passa o numero de casas a agregar</param>
        /// <returns>Devolve um array de strings</returns>
        private string[] NmeaParser(string sentence, int address, int fieldNumber)
        {
            // Divide a trama
            string[] fields = sentence.TrimStart('$').Split(',');

            // Faz a seleção dos campos consoante o endereço e o número de casas 
            var selectedFields = fields.Skip(address).Take(fieldNumber).ToArray();

            // Devolve os campos num array
            return selectedFields;
        }

        /// <summary>
        /// Método de gestão de equipamento
        /// </summary>
        /// <param name="dataSource">Passa a fonte de dados <see cref="DataSource"/></param>
        /// <param name="equi">Passa Equipamento <see cref="Equipment"/></param>
        /// <param name="lastValues">Passa string para registo de valores</param>
        /// <returns></returns>
        private async Task HandleEquipment(DataSource dataSource, Equipment equi, StringValuesWrapper lastValues)
        {

            // Chama método para ler trama (sentence) inicial
            string nmeaSentence = await ReadLineAsync();
            //Console.WriteLine($"A ler depois1 : {_serialPort.PortName} - {nmeaSentence}");

            // Enquanto a trama não iniciar com $nome
            // Evita o corte
            while (!String.Equals(nmeaSentence.Split(",")[0], $"${equi.Name.ToUpperInvariant()}"))
            {
                // Continua a devolver a trama até à condição
                nmeaSentence = await ReadLineAsync();
                //Console.WriteLine($"A ler depois 2 : {_serialPort.PortName} - {nmeaSentence}");
                if (String.IsNullOrEmpty(nmeaSentence)) break;

            }
            
            /// para cada equipamento, lista variáveis
            /// passa a connection string e id do equipamento atual
            /// leitura por base de dados ou config
            List<Variable> vars_list = _dbSource ? DatabaseHelper.GetVariables(_connString, equi.Id) : Worker.GetVariablesConfig(equi.Id, _configuration);

            /// lista tasks para cada variável
            /// chama método de leitura para cada uma
            var readingTasks = vars_list.Select(varE => ReadVariablesAsync<object>(equi.BoemName.ToUpperInvariant(), varE, lastValues, nmeaSentence)).ToList();
            await Task.WhenAll(readingTasks);

        }

        #endregion
    }

    #endregion


    #endregion
}


