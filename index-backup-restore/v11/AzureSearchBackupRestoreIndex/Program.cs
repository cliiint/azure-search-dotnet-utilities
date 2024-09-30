﻿// This is a prototype tool that allows for extraction of data from a search index
// Since this tool is still under development, it should not be used for production usage

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;

namespace AzureSearchBackupRestoreIndex;

class Program
{
	private static string SourceSearchServiceName;
	private static string SourceAdminKey;
	private static string SourceIndexName;
	private static string TargetSearchServiceName;
	private static string TargetAdminKey;
	private static string TargetIndexName;
	private static string BackupDirectory;

	private static SearchIndexClient SourceIndexClient;
	private static SearchClient SourceSearchClient;
	private static SearchIndexClient TargetIndexClient;
	private static SearchClient TargetSearchClient;

	/// <summary>
	/// StartDate and EndDate are used to determine range of documents to
	/// extract from index.
	/// </summary>
	private static DateTime StartDate;
	private static DateTime EndDate;
	private static DateTime CursorStartDate;

	private static int MaxBatchSize = 500;          // JSON files will contain this many documents / file and can be up to 1000
	private static int ParallelizationCount = 10;

	static void Main()
	{
		//Get source and target search service info and index names from appsettings.json file
		//Set up source and target search service clients
		ConfigurationSetup();

		//Backup the source index
		BackupIndexAndDocuments();

		//Recreate and import content to target index
		DeleteIndex();
		CreateTargetIndex();
		BatchUploadActions();

		Console.WriteLine("Press any key to continue...");
		Console.ReadLine();
	}

	static void ConfigurationSetup()
	{

		IConfigurationBuilder builder = new ConfigurationBuilder().AddJsonFile("appsettings.json");
		IConfigurationRoot configuration = builder.Build();

		StartDate = DateTime.Now;
		EndDate = DateTime.Now;
		CursorStartDate = StartDate;

		SourceSearchServiceName = configuration["SourceSearchServiceName"];
		SourceAdminKey = configuration["SourceAdminKey"];
		SourceIndexName = configuration["SourceIndexName"];
		TargetSearchServiceName = configuration["TargetSearchServiceName"];
		TargetAdminKey = configuration["TargetAdminKey"];
		TargetIndexName = configuration["TargetIndexName"];
		BackupDirectory = configuration["BackupDirectory"];

		Console.WriteLine("CONFIGURATION:");
		Console.WriteLine("\n  Source service and index {0}, {1}", SourceSearchServiceName, SourceIndexName);
		Console.WriteLine("\n  Target service and index: {0}, {1}", TargetSearchServiceName, TargetIndexName);
		Console.WriteLine("\n  Backup directory: " + BackupDirectory);

		SourceIndexClient = new SearchIndexClient(new Uri("https://" + SourceSearchServiceName + ".search.windows.net"), new AzureKeyCredential(SourceAdminKey));
		SourceSearchClient = SourceIndexClient.GetSearchClient(SourceIndexName);


		TargetIndexClient = new SearchIndexClient(new Uri($"https://" + TargetSearchServiceName + ".search.windows.net"), new AzureKeyCredential(TargetAdminKey));
		TargetSearchClient = TargetIndexClient.GetSearchClient(TargetIndexName);
	}

	static void BackupIndexAndDocuments()
	{
		// Backup the index schema to the specified backup directory
		Console.WriteLine("\n  Backing up source index schema to {0}\r\n", BackupDirectory + "\\" + SourceIndexName + ".schema");


		File.WriteAllText(Path.Combine(BackupDirectory, $"{SourceIndexName}.schema"), GetIndexSchema());

		// Extract the content to JSON files 
		int SourceDocCount = GetCurrentDocCount(SourceSearchClient);
		WriteIndexDocuments();     // Output content from index to json files
	}

	static void WriteIndexDocuments()
	{
		// Write document files in batches (per MaxBatchSize) in parallel
		while (DateTime.Compare(CursorStartDate, EndDate) < 0)
		{
			List<Task> tasks = new List<Task>();
			for (int job = 0; job < ParallelizationCount; job++)
			{
				if (DateTime.Compare(CursorStartDate, EndDate) < 0)
				{
					var start = CursorStartDate;
					var dateString = start.ToString("yyyy-MM-dd");

					Console.WriteLine("Backing up source documents to {0}", Path.Combine(BackupDirectory, $"{SourceIndexName}-{dateString}.json"));

					tasks.Add(Task.Factory.StartNew(() =>
						ExportToJSON(start, Path.Combine(BackupDirectory, $"{SourceIndexName}{dateString}.json"))
					));

					CursorStartDate = CursorStartDate.AddDays(1);
				}

			}
			Task.WaitAll(tasks.ToArray());  // Wait for all the stored procs in the group to complete
		}

		return;
	}

	static void ExportToJSON(DateTime Date, string FileName)
	{
		// Extract all the documents from the selected index to JSON files in batches of {MaxBatchSize} docs / file
		var startOfDay = Date.ToString("yyyy-MM-dd") + "T00:00:00Z";
		var startOfNextDay = Date.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00Z";
		var returnedCount = 0;
		var fileNameCounter = 0;

		string json = string.Empty;
		try
		{
			SearchOptions options = new SearchOptions()
			{
				SearchMode = SearchMode.All,
				Size = MaxBatchSize,
				Skip = 0,
				Filter = $"metadata_storage_last_modified ge {startOfDay} and metadata_storage_last_modified lt {startOfNextDay}",
				OrderBy = { "metadata_storage_last_modified asc" }
			};

			// possible server side pagination will occur (but shouldn't as long as size param
			// is provided. but might need to look out for that)
			SearchResults<SearchDocument> response = SourceSearchClient.Search<SearchDocument>("*", options);
			returnedCount = response.GetResults().Count();

			while (returnedCount > 0)
			{
				var fileName = FileName.Replace(".json", $"-{fileNameCounter}.json");
				foreach (var doc in response.GetResults())
				{
					json += JsonSerializer.Serialize(doc.Document) + ",";
					json = json.Replace("\"Latitude\":", "\"type\": \"Point\", \"coordinates\": [");
					json = json.Replace("\"Longitude\":", "");
					json = json.Replace(",\"IsEmpty\":false,\"Z\":null,\"M\":null,\"CoordinateSystem\":{\"EpsgId\":4326,\"Id\":\"4326\",\"Name\":\"WGS84\"}", "]");
					json += "\r\n";
				}

				// Output the formatted content to a file
				json = json.Substring(0, json.Length - 3); // remove trailing comma
				File.WriteAllText(fileName, "{\"value\": [");
				File.AppendAllText(fileName, json);
				File.AppendAllText(fileName, "]}");
				Console.WriteLine("  Total documents: {0}", response.GetResults().Count().ToString());
				json = string.Empty;

				options.Skip += MaxBatchSize;
				fileNameCounter += 1;
				response = SourceSearchClient.Search<SearchDocument>("*", options);
				returnedCount = response.GetResults().Count();
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine("Error: {0}", ex.Message);
		}
	}

	static string GetIDFieldName()
	{
		// Find the id field of this index
		string IDFieldName = string.Empty;
		try
		{
			var schema = SourceIndexClient.GetIndex(SourceIndexName);
			foreach (var field in schema.Value.Fields)
			{
				if (field.IsKey == true)
				{
					IDFieldName = Convert.ToString(field.Name);
					break;
				}
			}

		}
		catch (Exception ex)
		{
			Console.WriteLine("Error: {0}", ex.Message);
		}

		return IDFieldName;
	}

	static string GetIndexSchema()
	{
		// Extract the schema for this index
		// We use REST here because we can take the response as-is

		Uri ServiceUri = new Uri("https://" + SourceSearchServiceName + ".search.windows.net");
		HttpClient HttpClient = new HttpClient();
		HttpClient.DefaultRequestHeaders.Add("api-key", SourceAdminKey);

		string Schema = string.Empty;
		try
		{
			Uri uri = new Uri(ServiceUri, "/indexes/" + SourceIndexName);
			HttpResponseMessage response = AzureSearchHelper.SendSearchRequest(HttpClient, HttpMethod.Get, uri);
			AzureSearchHelper.EnsureSuccessfulSearchResponse(response);
			Schema = response.Content.ReadAsStringAsync().Result;
		}
		catch (Exception ex)
		{
			Console.WriteLine("Error: {0}", ex.Message);
		}

		return Schema;
	}

	private static bool DeleteIndex()
	{
		Console.WriteLine("\n  Delete target index {0} in {1} search service, if it exists", TargetIndexName, TargetSearchServiceName);
		// Delete the index if it exists
		try
		{
			TargetIndexClient.DeleteIndex(TargetIndexName);
		}
		catch (Exception ex)
		{
			Console.WriteLine("  Error deleting index: {0}\r\n", ex.Message);
			Console.WriteLine("  Did you remember to set your SearchServiceName and SearchServiceApiKey?\r\n");
			return false;
		}

		return true;
	}

	static void CreateTargetIndex()
	{
		Console.WriteLine("\n  Create target index {0} in {1} search service", TargetIndexName, TargetSearchServiceName);
		// Use the schema file to create a copy of this index

		string json = File.ReadAllText(Path.Combine(BackupDirectory, $"{SourceIndexName}.schema"));

		// Do some cleaning of this file to change index name, etc
		json = "{" + json.Substring(json.IndexOf("\"name\""));
		int indexOfIndexName = json.IndexOf("\"", json.IndexOf("name\"") + 5) + 1;
		int indexOfEndOfIndexName = json.IndexOf("\"", indexOfIndexName);
		json = json.Substring(0, indexOfIndexName) + TargetIndexName + json.Substring(indexOfEndOfIndexName);

		Uri ServiceUri = new Uri("https://" + TargetSearchServiceName + ".search.windows.net");
		HttpClient HttpClient = new HttpClient();
		HttpClient.DefaultRequestHeaders.Add("api-key", TargetAdminKey);

		try
		{
			Uri uri = new Uri(ServiceUri, "/indexes");
			HttpResponseMessage response = AzureSearchHelper.SendSearchRequest(HttpClient, HttpMethod.Post, uri, json);
			response.EnsureSuccessStatusCode();
		}
		catch (Exception ex)
		{
			Console.WriteLine("  Error: {0}", ex.Message);
		}
	}

	static int GetCurrentDocCount(SearchClient searchClient)
	{
		// Get the current doc count of the specified index
		try
		{
			SearchOptions options = new SearchOptions
			{
				SearchMode = SearchMode.All,
				IncludeTotalCount = true
			};

			SearchResults<Dictionary<string, object>> response = searchClient.Search<Dictionary<string, object>>("*", options);
			return Convert.ToInt32(response.TotalCount);
		}
		catch (Exception ex)
		{
			Console.WriteLine("  Error: {0}", ex.Message);
		}

		return -1;
	}

	static void BatchUploadActions()
	{
		HttpClient HttpClient = new HttpClient();
		HttpClient.DefaultRequestHeaders.Add("api-key", TargetAdminKey);

		var files = Directory.GetFiles(BackupDirectory, SourceIndexName + "*.json");
		var page = 0;
		var completed = 0;

		while (completed < files.Length)
		{
			var batch = files.Skip(page * ParallelizationCount).Take(ParallelizationCount);
			var tasks = new List<Task>();

			try
			{
				foreach (string fileName in batch)
				{
					tasks.Add(Task.Factory.StartNew(() =>
						UploadToIndex(HttpClient, fileName)
					));
				}

				Task.WaitAll(tasks.ToArray());

				page += 1;
				completed += batch.Count();
			}
			catch (Exception ex)
			{
				Console.WriteLine("  Error: {0}", ex.Message);
			}
		}
	}

	static void UploadToIndex(HttpClient httpClient, string fileName)
	{
		Console.WriteLine("\n  Upload index documents from saved JSON files");
		// Take JSON file and import this as-is to target index
		Uri ServiceUri = new Uri("https://" + TargetSearchServiceName + ".search.windows.net");

		Console.WriteLine("  -Uploading documents from file {0}", fileName);
		string json = File.ReadAllText(fileName);
		Uri uri = new Uri(ServiceUri, "/indexes/" + TargetIndexName + "/docs/index");
		HttpResponseMessage response = AzureSearchHelper.SendSearchRequest(httpClient, HttpMethod.Post, uri, json);
		response.EnsureSuccessStatusCode();
	}
}