using System;
using System.ServiceProcess;

public class MyService : ServiceBase
{
    public static readonly string SERVICE_ID = Guid.NewGuid().ToString();
    public MyService()
    {
        ServiceName = SERVICE_ID;
    }

    protected override void OnStart(string[] args)
    {
        Program.StartOnWindowService(args);
    }

    protected override void OnStop()
    {
        Program.Stop();
    }
}
