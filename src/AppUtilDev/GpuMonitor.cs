using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
namespace AppUtilDev;

public record GpuAdapter(string Name,string Luid,ulong DedicatedBytes);
public record GpuSample(string Name,double? Usage,ulong? DedicatedBytes,ulong Capacity)
{
 public string Label => Usage.HasValue?$"{Name} · {Usage.Value:0.#}%":"GPU · uso no disponible";
 public string Memory => DedicatedBytes.HasValue?$"VRAM {DedicatedBytes.Value/1073741824d:0.0} / {Capacity/1073741824d:0.#} GB":"VRAM no disponible";
}
public sealed class GpuMonitor
{
 [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid guid,out IntPtr factory);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters(IntPtr factory,uint index,out IntPtr adapter);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc(IntPtr adapter,out AdapterDesc desc);
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
 struct AdapterDesc
 {
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description;
  public uint VendorId,DeviceId,SubsystemId,Revision;
  public UIntPtr VideoMemory,SystemMemory,SharedMemory;
  public uint LuidLow; public int LuidHigh; public uint Flags;
 }
 GpuAdapter? adapter;
 public static List<GpuAdapter> Adapters()
 {
  var result=new List<GpuAdapter>(); var iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
  Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid,out var factory));
  try
  {
   var enumerate=Marshal.GetDelegateForFunctionPointer<EnumAdapters>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory),12*IntPtr.Size));
   for(uint i=0;;i++)
   {
    int hr=enumerate(factory,i,out var item);
    if(hr==unchecked((int)0x887A0002)) break;
    Marshal.ThrowExceptionForHR(hr);
    try
    {
     var describe=Marshal.GetDelegateForFunctionPointer<GetDesc>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(item),10*IntPtr.Size));
     Marshal.ThrowExceptionForHR(describe(item,out var desc));
     if((desc.Flags&2)==0) result.Add(new(desc.Description,$"luid_0x{unchecked((uint)desc.LuidHigh):x8}_0x{desc.LuidLow:x8}",desc.VideoMemory.ToUInt64()));
    } finally { Marshal.Release(item); }
   }
  } finally { Marshal.Release(factory); }
  return result;
 }
 public GpuSample Read()
 {
  try
  {
   adapter ??=Adapters().OrderByDescending(x=>x.DedicatedBytes).FirstOrDefault();
   if(adapter==null) return new("",null,null,0);
   var engines=new List<(string Name,double Usage)>();
   using(var query=new ManagementObjectSearcher("SELECT Name,UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
   using(var rows=query.Get()) foreach(ManagementObject row in rows) using(row)
    if(row["Name"] is string name && name.Contains(adapter.Luid,StringComparison.OrdinalIgnoreCase)) engines.Add((name,Convert.ToDouble(row["UtilizationPercentage"])));
   ulong? bytes=null;
   using(var query=new ManagementObjectSearcher("SELECT Name,DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
   using(var rows=query.Get()) foreach(ManagementObject row in rows) using(row)
    if(row["Name"] is string name && name.Contains(adapter.Luid,StringComparison.OrdinalIgnoreCase)) bytes=(bytes??0)+Convert.ToUInt64(row["DedicatedUsage"]);
   return new(adapter.Name,engines.Count>0?Aggregate(engines):null,bytes,adapter.DedicatedBytes);
  }
  catch { return new(adapter?.Name??"GPU",null,null,adapter?.DedicatedBytes??0); }
 }
 // Sum process counters for each physical engine, then use the busiest engine.
 public static double Aggregate(IEnumerable<(string Name,double Usage)> engines) => engines.GroupBy(x=>Regex.Match(x.Name,@"phys_\d+_eng_\d+").Value)
  .Select(g=>Math.Clamp(g.Sum(x=>x.Usage),0,100)).DefaultIfEmpty(0).Max();
}
