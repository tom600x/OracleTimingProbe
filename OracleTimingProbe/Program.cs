using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;

namespace OracleTimingProbe
{
	internal sealed class ProbeSettings
	{
		public string ConnectionString { get; set; }
		public string TestQuery { get; set; }
		public int ReadRowsMax { get; set; }
		public int Runs { get; set; }
		public string CsvOutputPath { get; set; }
	}

	internal static class Program
	{
		private const int TcpPreflightTimeoutMs = 3000;

		private static int Main()
		{
			try
			{
				var settings = LoadSettings();
				var preflight = RunPreflight(settings.ConnectionString);

				Console.WriteLine("Oracle Timing Probe (.NET Framework 4.8)");
				PrintEnvironmentInfo();
				PrintPreflight(preflight, settings.ConnectionString);
				Console.WriteLine("Runs: {0}", settings.Runs);
				Console.WriteLine("ReadRowsMax: {0}", settings.ReadRowsMax);
				Console.WriteLine(new string('-', 70));
				var records = new List<ProbeRunRecord>();

				for (var i = 1; i <= settings.Runs; i++)
				{
					var result = RunProbe(settings);
					records.Add(new ProbeRunRecord
					{
						Run = i,
						TimestampUtc = DateTime.UtcNow,
						Result = result
					});

					Console.WriteLine(
						"Run {0}: success={1} open_ms={2} exec_ms={3} read_ms={4} total_ms={5} rows={6}",
						i,
						result.Success,
						result.OpenConnectionMs,
						result.ExecuteMs,
						result.ReadMs,
						result.TotalMs,
						result.RowsRead);

					if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
					{
						Console.WriteLine("        error={0}", result.ErrorMessage);
						Console.WriteLine("        type={0}", result.ErrorType);
						if (!string.IsNullOrWhiteSpace(result.InnerErrorMessage))
						{
							Console.WriteLine("        inner={0}", result.InnerErrorMessage);
						}

						if (!string.IsNullOrWhiteSpace(result.StackTop))
						{
							Console.WriteLine("        stack={0}", result.StackTop);
						}
					}
				}

				var csvPath = ResolveCsvPath(settings.CsvOutputPath);
				WriteCsv(csvPath, preflight, records);
				Console.WriteLine(new string('-', 70));
				Console.WriteLine("CSV written: {0}", csvPath);

				return 0;
			}
			catch (Exception ex)
			{
				Console.WriteLine("Fatal error: {0}", ex.Message);
				return 1;
			}
		}

		private static void PrintEnvironmentInfo()
		{
			Console.WriteLine("Machine: {0}", Environment.MachineName);
			Console.WriteLine("User: {0}", Environment.UserName);
			Console.WriteLine("ProcessBitness: {0}", Environment.Is64BitProcess ? "x64" : "x86");
			Console.WriteLine("OS: {0}", Environment.OSVersion);
			Console.WriteLine("CLR: {0}", Environment.Version);
			Console.WriteLine("OracleDriver: {0}", typeof(OracleConnection).Assembly.FullName);
			Console.WriteLine("StartedUtc: {0:O}", DateTime.UtcNow);
		}

		private static void PrintPreflight(PreflightResult preflight, string connectionString)
		{
			Console.WriteLine("ConnectionString(masked): {0}", MaskSecrets(connectionString));
			Console.WriteLine("DataSourceHost: {0}", string.IsNullOrWhiteSpace(preflight.Host) ? "(not parsed)" : preflight.Host);
			Console.WriteLine("DataSourcePort: {0}", preflight.Port > 0 ? preflight.Port.ToString() : "(not parsed)");
			Console.WriteLine("DnsResolved: {0}", preflight.DnsResolved);
			if (!string.IsNullOrWhiteSpace(preflight.DnsAddresses))
			{
				Console.WriteLine("DnsAddresses: {0}", preflight.DnsAddresses);
			}

			Console.WriteLine("TcpConnect:{0}ms Success={1}", TcpPreflightTimeoutMs, preflight.TcpReachable);
			if (!string.IsNullOrWhiteSpace(preflight.PreflightError))
			{
				Console.WriteLine("PreflightError: {0}", preflight.PreflightError);
			}
		}

		private static PreflightResult RunPreflight(string connectionString)
		{
			var result = new PreflightResult();
			var endpoint = ExtractHostPort(connectionString);
			result.Host = endpoint.Host;
			result.Port = endpoint.Port;

			if (string.IsNullOrWhiteSpace(result.Host) || result.Port <= 0)
			{
				result.PreflightError = "Could not parse HOST/PORT from connection string.";
				return result;
			}

			try
			{
				var addresses = Dns.GetHostAddresses(result.Host);
				result.DnsResolved = addresses != null && addresses.Length > 0;
				if (result.DnsResolved)
				{
					result.DnsAddresses = string.Join(",", Array.ConvertAll(addresses, a => a.ToString()));
				}

				using (var client = new TcpClient())
				{
					var connectTask = client.ConnectAsync(result.Host, result.Port);
					var completed = connectTask.Wait(TcpPreflightTimeoutMs);
					result.TcpReachable = completed && client.Connected;
					if (!completed)
					{
						result.PreflightError = "TCP connect timed out.";
					}
				}
			}
			catch (Exception ex)
			{
				result.PreflightError = ex.Message;
			}

			return result;
		}

		private static EndpointInfo ExtractHostPort(string connectionString)
		{
			var hostMatch = Regex.Match(connectionString, @"\(HOST\s*=\s*([^\)]+)\)", RegexOptions.IgnoreCase);
			var portMatch = Regex.Match(connectionString, @"\(PORT\s*=\s*(\d+)\)", RegexOptions.IgnoreCase);

			if (hostMatch.Success && portMatch.Success)
			{
				int parsedPort;
				if (int.TryParse(portMatch.Groups[1].Value, out parsedPort))
				{
					return new EndpointInfo
					{
						Host = hostMatch.Groups[1].Value.Trim(),
						Port = parsedPort
					};
				}
			}

			return new EndpointInfo();
		}

		private static string MaskSecrets(string input)
		{
			if (string.IsNullOrWhiteSpace(input))
			{
				return string.Empty;
			}

			var masked = Regex.Replace(input, @"(?i)(password\s*=\s*)([^;]+)", "$1***");
			masked = Regex.Replace(masked, @"(?i)(user\s*id\s*=\s*)([^;]+)", "$1***");
			return masked;
		}

		private static ProbeSettings LoadSettings()
		{
			var configuration = new ConfigurationBuilder()
				.SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
				.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
				.Build();

			var section = configuration.GetSection("Probe");
			var settings = new ProbeSettings
			{
				ConnectionString = section["ConnectionString"],
				TestQuery = section["TestQuery"],
				ReadRowsMax = ParseIntOrDefault(section["ReadRowsMax"], 50),
				Runs = ParseIntOrDefault(section["Runs"], 3),
				CsvOutputPath = section["CsvOutputPath"]
			};

			if (string.IsNullOrWhiteSpace(settings.ConnectionString))
			{
				throw new InvalidOperationException("Probe:ConnectionString is required in appsettings.json");
			}

			if (string.IsNullOrWhiteSpace(settings.TestQuery))
			{
				throw new InvalidOperationException("Probe:TestQuery is required in appsettings.json");
			}

			if (settings.ReadRowsMax < 1)
			{
				settings.ReadRowsMax = 1;
			}

			if (settings.Runs < 1)
			{
				settings.Runs = 1;
			}

			return settings;
		}

		private static int ParseIntOrDefault(string value, int defaultValue)
		{
			int parsed;
			return int.TryParse(value, out parsed) ? parsed : defaultValue;
		}

		private static ProbeResult RunProbe(ProbeSettings settings)
		{
			var result = new ProbeResult();
			var totalSw = Stopwatch.StartNew();

			try
			{
				using (var conn = new OracleConnection(settings.ConnectionString))
				{
					var openSw = Stopwatch.StartNew();
					conn.Open();
					openSw.Stop();
					result.OpenConnectionMs = openSw.ElapsedMilliseconds;

					using (var cmd = conn.CreateCommand())
					{
						cmd.BindByName = true;
						cmd.CommandText = settings.TestQuery;

						var execSw = Stopwatch.StartNew();
						using (var reader = cmd.ExecuteReader())
						{
							execSw.Stop();
							result.ExecuteMs = execSw.ElapsedMilliseconds;

							var readSw = Stopwatch.StartNew();
							var rows = 0;
							while (rows < settings.ReadRowsMax && reader.Read())
							{
								rows++;
							}

							readSw.Stop();
							result.ReadMs = readSw.ElapsedMilliseconds;
							result.RowsRead = rows;
						}
					}
				}

				result.Success = true;
			}
			catch (Exception ex)
			{
				result.Success = false;
				result.ErrorMessage = ex.Message;
				result.ErrorType = ex.GetType().FullName;
				result.InnerErrorMessage = ex.InnerException != null ? ex.InnerException.Message : string.Empty;
				result.StackTop = GetTopStackFrame(ex);
			}
			finally
			{
				totalSw.Stop();
				result.TotalMs = totalSw.ElapsedMilliseconds;
			}

			return result;
		}

		private static string GetTopStackFrame(Exception ex)
		{
			if (string.IsNullOrWhiteSpace(ex.StackTrace))
			{
				return string.Empty;
			}

			var lines = ex.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			return lines.Length > 0 ? lines[0].Trim() : string.Empty;
		}

		private static string ResolveCsvPath(string configuredPath)
		{
			if (!string.IsNullOrWhiteSpace(configuredPath))
			{
				return Path.IsPathRooted(configuredPath)
					? configuredPath
					: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath);
			}

			var fileName = string.Format("probe-results-{0:yyyyMMdd-HHmmss}.csv", DateTime.UtcNow);
			return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
		}

		private static void WriteCsv(string path, PreflightResult preflight, IEnumerable<ProbeRunRecord> records)
		{
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
			}

			using (var writer = new StreamWriter(path, false))
			{
				writer.WriteLine("run,timestamp_utc,success,open_ms,exec_ms,read_ms,total_ms,rows,host,port,dns_resolved,dns_addresses,tcp_reachable,preflight_error,error_type,error_message,inner_error,stack_top");

				foreach (var record in records)
				{
					var r = record.Result;
					writer.WriteLine(string.Join(",",
						record.Run.ToString(),
						CsvEscape(record.TimestampUtc.ToString("O")),
						r.Success.ToString(),
						r.OpenConnectionMs.ToString(),
						r.ExecuteMs.ToString(),
						r.ReadMs.ToString(),
						r.TotalMs.ToString(),
						r.RowsRead.ToString(),
						CsvEscape(preflight.Host),
						preflight.Port.ToString(),
						preflight.DnsResolved.ToString(),
						CsvEscape(preflight.DnsAddresses),
						preflight.TcpReachable.ToString(),
						CsvEscape(preflight.PreflightError),
						CsvEscape(r.ErrorType),
						CsvEscape(r.ErrorMessage),
						CsvEscape(r.InnerErrorMessage),
						CsvEscape(r.StackTop)));
				}
			}
		}

		private static string CsvEscape(string value)
		{
			if (value == null)
			{
				return string.Empty;
			}

			var escaped = value.Replace("\"", "\"\"");
			return string.Format("\"{0}\"", escaped);
		}
	}

	internal sealed class ProbeRunRecord
	{
		public int Run { get; set; }
		public DateTime TimestampUtc { get; set; }
		public ProbeResult Result { get; set; }
	}

	internal sealed class EndpointInfo
	{
		public string Host { get; set; }
		public int Port { get; set; }
	}

	internal sealed class PreflightResult
	{
		public string Host { get; set; }
		public int Port { get; set; }
		public bool DnsResolved { get; set; }
		public string DnsAddresses { get; set; }
		public bool TcpReachable { get; set; }
		public string PreflightError { get; set; }
	}

	internal sealed class ProbeResult
	{
		public bool Success { get; set; }
		public long OpenConnectionMs { get; set; }
		public long ExecuteMs { get; set; }
		public long ReadMs { get; set; }
		public long TotalMs { get; set; }
		public int RowsRead { get; set; }
		public string ErrorMessage { get; set; }
		public string ErrorType { get; set; }
		public string InnerErrorMessage { get; set; }
		public string StackTop { get; set; }
	}
}
