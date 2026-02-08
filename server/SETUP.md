# Bakabooru Setup Guide

## Prerequisites
- .NET 10 SDK
- SQLite (included with EF Core)

## Running the Application

### 1. Database Setup
First, ensure the database is updated:
```bash
cd server
dotnet ef database update --project Bakabooru.Data --startup-project Bakabooru.Server
```

### 2. Start the API Server
Run the server project:
```bash
dotnet run --project Bakabooru.Server
```
The API will be available at `http://localhost:5119`.

**Endpoints:**
- `GET /api/libraries`
- `POST /api/libraries` (Add a folder to scan)
- `GET /api/posts`
- `GET /api/tagcategories`

### 3. Start the Scanner
In a separate terminal, run the scanner worker:
```bash
dotnet run --project Bakabooru.Scanner
```
The scanner runs periodically (default: every 1 hour) and scans all configured libraries.

## Client Development
The client (Angular) will be implemented in the `../client` directory (future work).
