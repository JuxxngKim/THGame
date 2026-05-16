using System.Reflection;

namespace TH.Common;

public abstract class Singleton<T> where T : class
{
    private static readonly Lazy<T> _instance = new(CreateInstance);
    public static T Instance => _instance.Value;

    private static T CreateInstance()
    {
        var ctor = typeof(T).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: Type.EmptyTypes, modifiers: null);

        if (ctor is null)
            throw new InvalidOperationException(
                $"{typeof(T).FullName} 에 NonPublic 기본 생성자가 필요합니다.");

        return (T)ctor.Invoke(null);
    }
}
