using Google.GenAI;
string project = Environment.GetEnvironmentVariable("PROJECT_ID") ?? "your-project-id";
string location = Environment.GetEnvironmentVariable("LOCATION") ?? "us-central1";

// Vertex AI API
var client = new Client(
    project: project,
    location: location,
    vertexAI: true
);

await foreach (var chunk in client.Models.GenerateContentStreamAsync(
        model: "gemini-2.5-flash",
        contents: "「空はなぜ青いの？」"
    ))
{
    Console.WriteLine(chunk.Candidates[0].Content.Parts[0].Text);
}
