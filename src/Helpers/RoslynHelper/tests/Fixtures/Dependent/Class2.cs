namespace Dependent;

public class Consumer
{
    public string Read() => new Good.Widget().Name;
}
