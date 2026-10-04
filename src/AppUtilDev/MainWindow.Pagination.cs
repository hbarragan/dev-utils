using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AppUtilDev;
public partial class MainWindow
{
 UsageRow[] allCodex=Array.Empty<UsageRow>(),allClaude=Array.Empty<UsageRow>();
 string[] allAdapters=Array.Empty<string>();
 int codexPage,claudePage,adapterPage;
 void RenderQuotaPages()
 {
  void Show(UsageRow[] items,ref int page,ItemsControl list,TextBlock label)
  {
   int pages=Math.Max(1,(items.Length+3)/4);page=Math.Clamp(page,0,pages-1);
   list.ItemsSource=items.Skip(page*4).Take(4).ToArray();label.Text=$"{page+1} / {pages}";
  }
  Show(allCodex,ref codexPage,CodexQuotas,CodexPage);Show(allClaude,ref claudePage,ClaudeQuotas,ClaudePage);
 }
 void QuotaPage_Click(object sender,RoutedEventArgs e)
 {
  var parts=((Button)sender).Tag.ToString()!.Split(':');int delta=int.Parse(parts[1]);
  if(parts[0]=="Codex")codexPage+=delta;else claudePage+=delta;RenderQuotaPages();
 }
 void RenderAdapterPage()
 {
  int pages=Math.Max(1,(allAdapters.Length+3)/4);adapterPage=Math.Clamp(adapterPage,0,pages-1);
  NetworkAdapters.ItemsSource=allAdapters.Skip(adapterPage*4).Take(4).ToArray();AdapterPage.Text=$"{adapterPage+1} / {pages}";
 }
 void AdapterPage_Click(object sender,RoutedEventArgs e){adapterPage+=int.Parse(((Button)sender).Tag.ToString()!);RenderAdapterPage();}
}
