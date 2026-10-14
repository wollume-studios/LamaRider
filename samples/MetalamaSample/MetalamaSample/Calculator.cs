namespace MetalamaSample;

[Id]
public class Calculator
{
    [Log]
    public int Add(int a, int b) => a + b;

    public int Multiply(int a, int b) => a * b;
}
