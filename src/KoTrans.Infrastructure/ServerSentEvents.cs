using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using KoTrans.Core;
namespace KoTrans.Infrastructure;

// Small shared SSE framing/byte bound, with provider-specific event validation in each parser.
internal static class ServerSentEvents
{
    internal static async IAsyncEnumerable<string> ReadAsync(Stream source,[EnumeratorCancellation] CancellationToken token)
    {
        using var limited=new LimitedStream(source);
        using var reader=new StreamReader(limited,new UTF8Encoding(false,true),detectEncodingFromByteOrderMarks:false);
        var data=new StringBuilder();bool firstLine=true;
        while(await reader.ReadLineAsync(token).ConfigureAwait(false) is {} line) {
            token.ThrowIfCancellationRequested();
            if(firstLine){line=line.TrimStart('\uFEFF');firstLine=false;}
            if(line.StartsWith("data:",StringComparison.Ordinal)) { data.AppendLine(line[5..].TrimStart());continue; }
            if(line.Length!=0 || data.Length==0) continue;
            var value=data.ToString().Trim();data.Clear();if(value.Length>0)yield return value;
        }
        // An unfinished event at EOF is not an acknowledged completion.
    }
    private sealed class LimitedStream(Stream inner):Stream
    {
        private long received;
        private int Count(int n){if((received+=n)>2_097_152)throw new TranslationException("AI 응답 크기 제한을 초과했습니다.");return n;}
        public override int Read(byte[] buffer,int offset,int count)=>Count(inner.Read(buffer,offset,count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)=>Count(await inner.ReadAsync(buffer,token).ConfigureAwait(false));
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
