import { checkPhotoFile, checkPhotoFiles } from './photos';

const file = (name: string, type: string, size = 1000) => new File([new Uint8Array(size)], name, { type });

describe('checkPhotoFile (BR12)', () => {
  it('accepts JPG and PNG up to 5 MB', () => {
    expect(checkPhotoFile(file('tap.jpg', 'image/jpeg'))).toBeNull();
    expect(checkPhotoFile(file('tap.png', 'image/png'))).toBeNull();
  });

  it('refuses other types, empty and oversized files', () => {
    expect(checkPhotoFile(file('scan.pdf', 'application/pdf'))).toContain('only JPG or PNG');
    expect(checkPhotoFile(file('tap.jpg', 'image/jpeg', 0))).toContain('empty');
    expect(checkPhotoFile(file('big.jpg', 'image/jpeg', 5 * 1024 * 1024 + 1))).toContain('larger than 5 MB');
  });

  it('keeps to five photos in total', () => {
    const chosen = [1, 2, 3].map((n) => file(`p${n}.jpg`, 'image/jpeg'));
    const { ok, problems } = checkPhotoFiles(chosen, 3);
    expect(ok.map((f) => f.name)).toEqual(['p1.jpg', 'p2.jpg']);
    expect(problems).toEqual(['p3.jpg: at most 5 photos.']);
  });
});
