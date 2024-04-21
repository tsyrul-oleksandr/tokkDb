using TokkDb.Core.Pages.Footers;
using TokkDb.Core.Pages.Headers;

namespace TokkDb.Core.Pages;

public class BasePage  {
  private PageHeader _header;
  private PageFooter _footer;

  public PageHeader Header => _header ??= CreateHeader();
  public PageFooter Footer => _footer ??= CreateFooter();

  public BasePage(PageBuffer tokkBuffer) {
    
  }
  
  protected virtual PageHeader CreateHeader() {
    return new PageHeader();
  }
  
  protected virtual PageFooter CreateFooter() {
    return new PageFooter();
  }
  
  
  
}
